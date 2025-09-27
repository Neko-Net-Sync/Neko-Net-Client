/*
     Neko-Net Client — PlayerData.Pairs.Pair
     ---------------------------------------
     Purpose
     - Represents one user pairing (direct and/or group-linked), holding the current relationship state,
         permissions, cached character data, and the associated in-game handler.
     - Bridges UI, apply pipeline, and configuration (notes/tags) across multi-service setups via optional API URL override.

     Behavior overview
     - Maintains an optional <see cref="PairHandler"/> (created on demand) that performs downloads and in-game application.
     - Tracks last received character data for re-application on visibility return or when permissions allow.
     - Filters mod content by effective permissions before applying to ensure compliance (animations, sounds, VFX).
     - Exposes context menu entries for quick actions (open profile, reapply, change permissions, cycle pause).

     Concurrency
     - Uses a semaphore to serialize creation/disposal of the handler and protect against race conditions from
         rapidly changing presence or apply triggers.
*/
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Microsoft.Extensions.Logging;
using NekoNet.API.Data;
using NekoNet.API.Data.Enum;
using NekoNet.API.Data.Extensions;
using NekoNet.API.Dto.User;
using NekoNetClient.PlayerData.Factories;
using NekoNetClient.PlayerData.Handlers;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.Utils;
using NekoNetClient.Services.Events;

namespace NekoNetClient.PlayerData.Pairs;

/// <summary>
/// Represents a single paired user within the client, including relationship status, permissions,
/// cached data, and an optional in-game <c>PairHandler</c> responsible for downloads and application.
/// <para>
/// The pair coordinates permission-aware application of character data and ensures that operations
/// such as handler creation/disposal are performed safely under a semaphore to avoid race conditions.
/// </para>
/// </summary>
public class Pair
{
    private readonly PairHandlerFactory _cachedPlayerFactory;
    private readonly SemaphoreSlim _creationSemaphore = new(1);
    private readonly ILogger<Pair> _logger;
    private readonly MareMediator _mediator;
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly string? _apiUrlOverride;
    private CancellationTokenSource _applicationCts = new();
    private OnlineUserIdentDto? _onlineUserIdentDto = null;
    private volatile bool _isApplying = false;
    private volatile bool _deferCleanupOnce = false;

    public Pair(ILogger<Pair> logger, UserFullPairDto userPair, PairHandlerFactory cachedPlayerFactory,
        MareMediator mediator, ServerConfigurationManager serverConfigurationManager, string? apiUrlOverride = null)
    {
        _logger = logger;
        UserPair = userPair;
        _cachedPlayerFactory = cachedPlayerFactory;
        _mediator = mediator;
        _serverConfigurationManager = serverConfigurationManager;
        _apiUrlOverride = apiUrlOverride;
    }

    /// <summary>
    /// True when an active <c>PairHandler</c> exists and a resolved player identity is known.
    /// </summary>
    public bool HasCachedPlayer => CachedPlayer != null && !string.IsNullOrEmpty(CachedPlayer.PlayerName) && _onlineUserIdentDto != null;
    public IndividualPairStatus IndividualPairStatus => UserPair.IndividualPairStatus;
    public bool IsDirectlyPaired => IndividualPairStatus != IndividualPairStatus.None;
    public bool IsOneSidedPair => IndividualPairStatus == IndividualPairStatus.OneSided;
    public bool IsOnline => CachedPlayer != null;

    public bool IsPaired => IndividualPairStatus == IndividualPairStatus.Bidirectional || UserPair.Groups.Any();
    public bool IsPaused => UserPair.OwnPermissions.IsPaused();
    /// <summary>
    /// Whether the paired character is currently visible in the world (as seen by the live handler).
    /// </summary>
    public bool IsVisible => CachedPlayer?.IsVisible ?? false;
    /// <summary>
    /// Whether an application pipeline is currently in progress for this pair (used to gate UI actions).
    /// </summary>
    public bool IsApplying => _isApplying;
    public bool DeferCleanupOnce => _deferCleanupOnce;
    public CharacterData? LastReceivedCharacterData { get; set; }
    public string? PlayerName => CachedPlayer?.PlayerName ?? string.Empty;
    public long LastAppliedDataBytes => CachedPlayer?.LastAppliedDataBytes ?? -1;
    public long LastAppliedDataTris { get; set; } = -1;
    public long LastAppliedApproximateVRAMBytes { get; set; } = -1;
    public string Ident => _onlineUserIdentDto?.Ident ?? string.Empty;

    public UserData UserData => UserPair.User;

    public UserFullPairDto UserPair { get; set; }
    private PairHandler? CachedPlayer { get; set; }

    /// <summary>
    /// The API URL override for service-scoped pairs. When set, notes/tags and some UI labels reflect the service.
    /// </summary>
    public string? ApiUrlOverride => _apiUrlOverride;

    /// <summary>
    /// Integrates context menu entries for actions related to this pair when right-clicking the in-game character.
    /// </summary>
    public void AddContextMenu(IMenuOpenedArgs args)
    {
        if (CachedPlayer == null || (args.Target is not MenuTargetDefault target) || target.TargetObjectId != CachedPlayer.PlayerCharacterId) return;

        SeStringBuilder seStringBuilder = new();
        SeStringBuilder seStringBuilder2 = new();
        SeStringBuilder seStringBuilder3 = new();
        SeStringBuilder seStringBuilder4 = new();
        var openProfileSeString = seStringBuilder.AddText("Open Profile").Build();
        var reapplyDataSeString = seStringBuilder2.AddText("Reapply last data").Build();
        var cyclePauseState = seStringBuilder3.AddText("Cycle pause state").Build();
        var changePermissions = seStringBuilder4.AddText("Change Permissions").Build();
        if (!IsPaused)
        {
            args.AddMenuItem(new MenuItem()
            {
                Name = openProfileSeString,
                OnClicked = (a) => _mediator.Publish(new ProfileOpenStandaloneMessage(this)),
                UseDefaultPrefix = false,
                PrefixChar = 'M',
                PrefixColor = 526
            });
        }

        if (!IsPaused)
        {
            args.AddMenuItem(new MenuItem()
            {
                Name = reapplyDataSeString,
                OnClicked = (a) => ApplyLastReceivedData(forced: true),
                UseDefaultPrefix = false,
                PrefixChar = 'M',
                PrefixColor = 526
            });
        }

        args.AddMenuItem(new MenuItem()
        {
            Name = changePermissions,
            OnClicked = (a) => _mediator.Publish(new OpenPermissionWindow(this)),
            UseDefaultPrefix = false,
            PrefixChar = 'M',
            PrefixColor = 526
        });

        args.AddMenuItem(new MenuItem()
        {
            Name = cyclePauseState,
            OnClicked = (a) => _mediator.Publish(new CyclePauseMessage(UserData)),
            UseDefaultPrefix = false,
            PrefixChar = 'M',
            PrefixColor = 526
        });
    }

    /// <summary>
    /// Receives live character data and triggers an application. If the handler is not yet created,
    /// the data is buffered and applied once the handler appears or times out.
    /// </summary>
    public void ApplyData(OnlineUserCharaDataDto data)
    {
        _applicationCts = _applicationCts.CancelRecreate();
        LastReceivedCharacterData = data.CharaData;

        if (CachedPlayer == null)
        {
            _logger.LogDebug("Received Data for {uid} but CachedPlayer does not exist, waiting", data.User.UID);
            _ = Task.Run(async () =>
            {
                using var timeoutCts = new CancellationTokenSource();
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
                var appToken = _applicationCts.Token;
                using var combined = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, appToken);
                while (CachedPlayer == null && !combined.Token.IsCancellationRequested)
                {
                    await Task.Delay(250, combined.Token).ConfigureAwait(false);
                }

                if (!combined.IsCancellationRequested)
                {
                    _logger.LogDebug("Applying delayed data for {uid}", data.User.UID);
                    ApplyLastReceivedData();
                }
            });
            return;
        }

        ApplyLastReceivedData();
    }

    /// <summary>
    /// Applies the last received character data, honoring permission filters. When <paramref name="forced"/>
    /// is true, forces a reapply even if hashes match to recover from transient states.
    /// </summary>
    public void ApplyLastReceivedData(bool forced = false)
    {
        if (CachedPlayer == null) return;
        if (LastReceivedCharacterData == null) return;

        CachedPlayer.ApplyCharacterData(Guid.NewGuid(), RemoveNotSyncedFiles(LastReceivedCharacterData.DeepClone())!, forced);
    }

    /// <summary>
    /// Sets the internal applying flag to inform UI and defuse actions that could clash during application.
    /// </summary>
    internal void SetApplying(bool applying)
    {
        _isApplying = applying;
    }

    /// <summary>
    /// Mark that the next cleanup (e.g., after pausing) should be deferred entirely to avoid freezing.
    /// </summary>
    public void MarkDeferCleanupOnce()
    {
        _deferCleanupOnce = true;
    }

    /// <summary>
    /// Consume the defer flag once; returns true if a deferral was requested.
    /// </summary>
    internal bool ConsumeDeferCleanupOnce()
    {
        if (_deferCleanupOnce)
        {
            _deferCleanupOnce = false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Ensures a <see cref="PairHandler"/> exists for this pair and initializes it with the latest ident.
    /// Protected by a semaphore to avoid duplicate creation under race.
    /// </summary>
    public void CreateCachedPlayer(OnlineUserIdentDto? dto = null)
    {
        try
        {
            _creationSemaphore.Wait();

            if (CachedPlayer != null) return;

            if (dto == null && _onlineUserIdentDto == null)
            {
                CachedPlayer?.Dispose();
                CachedPlayer = null;
                return;
            }
            if (dto != null)
            {
                _onlineUserIdentDto = dto;
            }

            CachedPlayer?.Dispose();
            CachedPlayer = _cachedPlayerFactory.Create(this);
        }
        finally
        {
            _creationSemaphore.Release();
        }
    }

    /// <summary>
    /// Gets the configured note/tag for this UID, taking service scope into account if applicable.
    /// </summary>
    public string? GetNote()
    {
        if (_apiUrlOverride == null) return _serverConfigurationManager.GetNoteForUid(UserData.UID);
        return _serverConfigurationManager.GetNoteForUidForApiUrl(_apiUrlOverride, UserData.UID);
    }

    /// <summary>
    /// Returns the hash used to locate the player in the object table, or empty if unknown.
    /// </summary>
    public string GetPlayerNameHash()
    {
        return CachedPlayer?.PlayerNameHash ?? string.Empty;
    }

    /// <summary>
    /// Whether the pair has any connection (direct or via groups).
    /// </summary>
    public bool HasAnyConnection()
    {
        return UserPair.Groups.Any() || UserPair.IndividualPairStatus != IndividualPairStatus.None;
    }

    /// <summary>
    /// Marks this pair as offline and disposes its handler. Optionally logs an informational reason.
    /// </summary>
    public void MarkOffline(bool wait = true, string? reason = null)
    {
        try
        {
            if (wait)
                _creationSemaphore.Wait();
            LastReceivedCharacterData = null;
            var player = CachedPlayer;
            CachedPlayer = null;
            player?.Dispose();
            _onlineUserIdentDto = null;

            if (!string.IsNullOrEmpty(reason))
            {
                try
                {
                    _mediator.Publish(new EventMessage(new Event(PlayerName ?? string.Empty, UserData, nameof(Pair), EventSeverity.Informational,
                        reason) { Server = _apiUrlOverride.ToServerLabel() }));
                }
                catch { }
            }
        }
        finally
        {
            if (wait)
                _creationSemaphore.Release();
        }
    }

    /// <summary>
    /// Sets the note/tag for this pair UID, taking service scope into account if applicable.
    /// </summary>
    public void SetNote(string note)
    {
        if (_apiUrlOverride == null)
            _serverConfigurationManager.SetNoteForUid(UserData.UID, note);
        else
            _serverConfigurationManager.SetNoteForUidForApiUrl(_apiUrlOverride, UserData.UID, note);
    }

    internal void SetIsUploading()
    {
        CachedPlayer?.SetUploading();
    }

    /// <summary>
    /// Filters out content not permitted by effective permissions (animations, sounds, VFX) from the
    /// character data prior to application.
    /// </summary>
    private CharacterData? RemoveNotSyncedFiles(CharacterData? data)
    {
        _logger.LogTrace("Removing not synced files");
        if (data == null)
        {
            _logger.LogTrace("Nothing to remove");
            return data;
        }

        bool disableIndividualAnimations = (UserPair.OtherPermissions.IsDisableAnimations() || UserPair.OwnPermissions.IsDisableAnimations());
        bool disableIndividualVFX = (UserPair.OtherPermissions.IsDisableVFX() || UserPair.OwnPermissions.IsDisableVFX());
        bool disableIndividualSounds = (UserPair.OtherPermissions.IsDisableSounds() || UserPair.OwnPermissions.IsDisableSounds());

        _logger.LogTrace("Disable: Sounds: {disableIndividualSounds}, Anims: {disableIndividualAnims}; " +
            "VFX: {disableGroupSounds}",
            disableIndividualSounds, disableIndividualAnimations, disableIndividualVFX);

        if (disableIndividualAnimations || disableIndividualSounds || disableIndividualVFX)
        {
            _logger.LogTrace("Data cleaned up: Animations disabled: {disableAnimations}, Sounds disabled: {disableSounds}, VFX disabled: {disableVFX}",
                disableIndividualAnimations, disableIndividualSounds, disableIndividualVFX);
            foreach (var objectKind in data.FileReplacements.Select(k => k.Key))
            {
                if (disableIndividualSounds)
                    data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                        .Where(f => !f.GamePaths.Any(p => p.EndsWith("scd", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                if (disableIndividualAnimations)
                    data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                        .Where(f => !f.GamePaths.Any(p => p.EndsWith("tmb", StringComparison.OrdinalIgnoreCase) || p.EndsWith("pap", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                if (disableIndividualVFX)
                    data.FileReplacements[objectKind] = data.FileReplacements[objectKind]
                        .Where(f => !f.GamePaths.Any(p => p.EndsWith("atex", StringComparison.OrdinalIgnoreCase) || p.EndsWith("avfx", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
            }
        }

        return data;
    }
}
