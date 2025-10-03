/*
     Neko-Net Client — PlayerData.Handlers.PairHandler
     --------------------------------------------------
     Purpose
     - Orchestrates the end-to-end pipeline for a single pair: from character data updates to downloading
         required files and applying them in-game through IPC with Penumbra, Glamourer, and related plugins.

     Core responsibilities
     - Visibility tracking: toggles visible state and publishes UI events; triggers reapply on return.
     - Download pipeline: computes missing files, verifies local cache vs server raw sizes when appropriate,
         and performs multi-CDN aware downloads through FileDownloadManager.
     - Apply pipeline: assigns temporary collections, sets manipulations/mod paths, and applies customization
         across all object kinds (player, pet, minion/mount, companion), with robust cancellation and redraws.
     - IPC coordination: wraps calls to Penumbra, Glamourer, Customize+, Honorific, Heels, Moodles, and PetNames.
     - Zoning/cutscene handling: cancels or defers work safely during combat, performances, zoning, GPose or cutscenes.

     Concurrency and stability
     - Deduplicates rapid ApplyCharacterData triggers and prevents mid-apply permission flips by marking the Pair busy.
     - Separates download and apply cancellation tokens and blocks overlapping applications for the same pair.
     - Defers cleanup/redraw work asynchronously during disposal to avoid blocking the game render thread.
*/
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NekoNet.API.Data;
using NekoNetClient.FileCache;
using NekoNet.API.Dto.Files;
using NekoNetClient.Interop.Ipc;
using NekoNetClient.PlayerData.Data;
using NekoNetClient.PlayerData.Factories;
using NekoNetClient.PlayerData.Pairs;
using NekoNetClient.Services;
using NekoNetClient.Services.Events;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.Utils;
using NekoNetClient.WebAPI.Files;
using System.Collections.Concurrent;
using System.Diagnostics;
using CharacterData = NekoNet.API.Data.CharacterData;
using ObjectKind = NekoNet.API.Data.Enum.ObjectKind;

namespace NekoNetClient.PlayerData.Handlers;

/// <summary>
/// Handles download and application of character data for a specific <see cref="Pairs.Pair"/>.
/// Tracks visibility, integrates with IPC providers, and coordinates safe application across gameplay states.
/// </summary>
public sealed class PairHandler : DisposableMediatorSubscriberBase
{
    private sealed record CombatData(Guid ApplicationId, CharacterData CharacterData, bool Forced);

    private readonly DalamudUtilService _dalamudUtil;
    private readonly FileDownloadManager _downloadManager;
    private readonly FileCacheManager _fileDbManager;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly IpcManager _ipcManager;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly PlayerPerformanceService _playerPerformanceService;
    private readonly PersonDownloadCoordinator _personDownloadCoordinator;
    private readonly PersonApplyCoordinator _personApplyCoordinator;
    private readonly IAppearancePresenceManager _presence;
    private readonly ServerConfigurationManager _serverConfigManager;
    private readonly PluginWarningNotificationService _pluginWarningNotificationManager;
    private CancellationTokenSource? _applicationCancellationTokenSource = new();
    private Guid _applicationId;
    private Task? _applicationTask;
    private CharacterData? _cachedData = null;
    // Throttle for re-visibility data refresh pulls
    private DateTime _lastVisibilityDataRequestUtc = DateTime.MinValue;
    private GameObjectHandler? _charaHandler;
    private readonly Dictionary<ObjectKind, Guid?> _customizeIds = [];
    private CombatData? _dataReceivedInDowntime;
    private CancellationTokenSource? _downloadCancellationTokenSource = new();
    private bool _forceApplyMods = false;
    private bool _isVisible;
    private Guid _penumbraCollection;
    private bool _redrawOnNextApplication = false;
    // In-flight guard per pair to avoid duplicate apply/download pipelines. Stores last start time.
    private DateTime _lastPipelineStartUtc = DateTime.MinValue;

    public PairHandler(ILogger<PairHandler> logger, Pair pair,
        GameObjectHandlerFactory gameObjectHandlerFactory,
        IpcManager ipcManager, FileDownloadManager transferManager,
        PluginWarningNotificationService pluginWarningNotificationManager,
        DalamudUtilService dalamudUtil, IHostApplicationLifetime lifetime,
        FileCacheManager fileDbManager, MareMediator mediator,
        PlayerPerformanceService playerPerformanceService,
        PersonDownloadCoordinator personDownloadCoordinator,
        PersonApplyCoordinator personApplyCoordinator,
        ServerConfigurationManager serverConfigManager, IAppearancePresenceManager presence) : base(logger, mediator)
    {
        Pair = pair;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _ipcManager = ipcManager;
        _downloadManager = transferManager;
        _pluginWarningNotificationManager = pluginWarningNotificationManager;
        _dalamudUtil = dalamudUtil;
        _lifetime = lifetime;
        _fileDbManager = fileDbManager;
    _playerPerformanceService = playerPerformanceService;
    _personDownloadCoordinator = personDownloadCoordinator;
        _personApplyCoordinator = personApplyCoordinator;
    _serverConfigManager = serverConfigManager;
    _presence = presence;
        _penumbraCollection = _ipcManager.Penumbra.CreateTemporaryCollectionAsync(logger, Pair.UserData.UID).ConfigureAwait(false).GetAwaiter().GetResult();

        Mediator.Subscribe<FrameworkUpdateMessage>(this, (_) => FrameworkUpdate());
        Mediator.Subscribe<ZoneSwitchStartMessage>(this, (_) =>
        {
            _downloadCancellationTokenSource?.CancelDispose();
            _charaHandler?.Invalidate();
            IsVisible = false;
        });
        Mediator.Subscribe<PenumbraInitializedMessage>(this, (_) =>
        {
            _penumbraCollection = _ipcManager.Penumbra.CreateTemporaryCollectionAsync(logger, Pair.UserData.UID).ConfigureAwait(false).GetAwaiter().GetResult();
            if (!IsVisible && _charaHandler != null)
            {
                PlayerName = string.Empty;
                _charaHandler.Dispose();
                _charaHandler = null;
            }
        });
        Mediator.Subscribe<ClassJobChangedMessage>(this, (msg) =>
        {
            if (msg.GameObjectHandler == _charaHandler)
            {
                _redrawOnNextApplication = true;
            }
        });
        Mediator.Subscribe<CombatOrPerformanceEndMessage>(this, (msg) =>
        {
            if (IsVisible && _dataReceivedInDowntime != null)
            {
                ApplyCharacterData(_dataReceivedInDowntime.ApplicationId,
                    _dataReceivedInDowntime.CharacterData, _dataReceivedInDowntime.Forced);
                _dataReceivedInDowntime = null;
            }
        });
        Mediator.Subscribe<CombatOrPerformanceStartMessage>(this, _ =>
        {
            _dataReceivedInDowntime = null;
            _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate();
            _applicationCancellationTokenSource = _applicationCancellationTokenSource?.CancelRecreate();
        });

        // No external revert-by-applyKey subscription; presence is handled per-handler disposal and cross-service online tracking.

        LastAppliedDataBytes = -1;
    }

    /// <summary>
    /// Whether the paired character is currently visible. Setting this publishes UI events and server labels.
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                string text = "User Visibility Changed, now: " + (_isVisible ? "Is Visible" : "Is not Visible");
                Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler),
                    EventSeverity.Informational, text) { Server = (Pair.ApiUrlOverride).ToServerLabel() }));
                Mediator.Publish(new RefreshUiMessage());
            }
        }
    }

    public long LastAppliedDataBytes { get; private set; }
    public Pair Pair { get; private set; }
    public nint PlayerCharacter => _charaHandler?.Address ?? nint.Zero;
    public unsafe uint PlayerCharacterId => (_charaHandler?.Address ?? nint.Zero) == nint.Zero
        ? uint.MaxValue
        : ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)_charaHandler!.Address)->EntityId;
    public string? PlayerName { get; private set; }
    public string PlayerNameHash => Pair.Ident;

    private static string BuildApplyKey(string? name, int? homeWorldId, string? serviceUid)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return $"{name.Trim().ToLowerInvariant()}@{homeWorldId.GetValueOrDefault(0)}";
        return $"uid:{serviceUid ?? "unknown"}";
    }

    private static string BuildServiceKey(string serviceApiBase, int serverIndex)
        => $"{serviceApiBase.Trim().ToLowerInvariant()}#{serverIndex}";

    /// <summary>
    /// Entry point for applying character data. Handles gameplay state checks (combat, cutscene, GPose),
    /// dedupes fast repeat triggers, and kicks off download/apply pipelines as needed.
    /// </summary>
    public void ApplyCharacterData(Guid applicationBase, CharacterData characterData, bool forceApplyCustomization = false)
    {
        // Cross-service apply serialization and duplicate suppression by UID+hash
        var uid = Pair.UserData.UID;
        var hash = characterData.DataHash.Value ?? string.Empty;
        var appCts = _applicationCancellationTokenSource ??= new CancellationTokenSource();
        bool acquired;
        try
        {
            acquired = _personApplyCoordinator.TryEnterAsync(uid, hash, appCts.Token).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!acquired)
        {
            Logger.LogTrace("[BASE-{appbase}] Skip apply for {uid} at {hash}, already current", applicationBase, uid, hash);
            return;
        }

        // Dedupe fast repeat triggers (e.g., double events on service/cross) within 750ms window
        var now = DateTime.UtcNow;
        if ((now - _lastPipelineStartUtc) < TimeSpan.FromMilliseconds(750))
        {
            Logger.LogTrace("[BASE-{appbase}] Skipping duplicate ApplyCharacterData trigger (within 750ms)", applicationBase);
            _personApplyCoordinator.Release(uid);
            return;
        }
        _lastPipelineStartUtc = now;

        if (_dalamudUtil.IsInCombatOrPerforming)
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: you are in combat or performing music, deferring application") { Server = (Pair.ApiUrlOverride).ToServerLabel() }));
            Logger.LogDebug("[BASE-{appBase}] Received data but player is in combat or performing", applicationBase);
            _dataReceivedInDowntime = new(applicationBase, characterData, forceApplyCustomization);
            SetUploading(isUploading: false);
            _personApplyCoordinator.Release(uid);
            return;
        }

        if (_charaHandler == null || (PlayerCharacter == IntPtr.Zero))
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: Receiving Player is in an invalid state, deferring application") { Server = (Pair.ApiUrlOverride).ToServerLabel() }));
            Logger.LogDebug("[BASE-{appBase}] Received data but player was in invalid state, charaHandlerIsNull: {charaIsNull}, playerPointerIsNull: {ptrIsNull}",
                applicationBase, _charaHandler == null, PlayerCharacter == IntPtr.Zero);
            var hasDiffMods = characterData.CheckUpdatedData(applicationBase, _cachedData, Logger,
                this, forceApplyCustomization, forceApplyMods: false)
                .Any(p => p.Value.Contains(PlayerChanges.ModManip) || p.Value.Contains(PlayerChanges.ModFiles));
            _forceApplyMods = hasDiffMods || _forceApplyMods || (PlayerCharacter == IntPtr.Zero && _cachedData == null);
            _cachedData = characterData;
            Logger.LogDebug("[BASE-{appBase}] Setting data: {hash}, forceApplyMods: {force}", applicationBase, _cachedData.DataHash.Value, _forceApplyMods);
            _personApplyCoordinator.Release(uid);
            return;
        }

        SetUploading(isUploading: false);

        Logger.LogDebug("[BASE-{appbase}] Applying data for {player}, forceApplyCustomization: {forced}, forceApplyMods: {forceMods}", applicationBase, this, forceApplyCustomization, _forceApplyMods);
        Logger.LogDebug("[BASE-{appbase}] Hash for data is {newHash}, current cache hash is {oldHash}", applicationBase, characterData.DataHash.Value, _cachedData?.DataHash.Value ?? "NODATA");

        if (string.Equals(characterData.DataHash.Value, _cachedData?.DataHash.Value ?? string.Empty, StringComparison.Ordinal) && !forceApplyCustomization)
        {
            _personApplyCoordinator.Release(uid);
            return;
        }

        if (_dalamudUtil.IsInCutscene || _dalamudUtil.IsInGpose || !_ipcManager.Penumbra.APIAvailable || !_ipcManager.Glamourer.APIAvailable)
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Warning,
                "Cannot apply character data: you are in GPose, a Cutscene or Penumbra/Glamourer is not available") { Server = (Pair.ApiUrlOverride).ToServerLabel() }));
            Logger.LogInformation("[BASE-{appbase}] Application of data for {player} while in cutscene/gpose or Penumbra/Glamourer unavailable, returning", applicationBase, this);
            _personApplyCoordinator.Release(uid);
            return;
        }

        // Mark this pair busy to prevent mid-apply permission flips causing crashes
        Pair.SetApplying(true);
        Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
            "Applying Character Data") { Server = (Pair.ApiUrlOverride).ToServerLabel() }));

        _forceApplyMods |= forceApplyCustomization;

        var charaDataToUpdate = characterData.CheckUpdatedData(applicationBase, _cachedData?.DeepClone() ?? new(), Logger, this, forceApplyCustomization, _forceApplyMods);

        if (_charaHandler != null && _forceApplyMods)
        {
            _forceApplyMods = false;
        }

        if (_redrawOnNextApplication && charaDataToUpdate.TryGetValue(ObjectKind.Player, out var player))
        {
            player.Add(PlayerChanges.ForcedRedraw);
            _redrawOnNextApplication = false;
        }

        if (charaDataToUpdate.TryGetValue(ObjectKind.Player, out var playerChanges))
        {
            _pluginWarningNotificationManager.NotifyForMissingPlugins(Pair.UserData, PlayerName!, playerChanges);
        }

        Logger.LogDebug("[BASE-{appbase}] Downloading and applying character for {name}", applicationBase, this);

        DownloadAndApplyCharacter(applicationBase, characterData.DeepClone(), charaDataToUpdate);
        // Mark as completed/satisfied for this hash now that the pipeline has been scheduled.
        _personApplyCoordinator.Complete(uid, hash);
        _personApplyCoordinator.Release(uid);
    }

    public override string ToString()
    {
        return Pair == null
            ? base.ToString() ?? string.Empty
            : Pair.UserData.AliasOrUID + ":" + PlayerName + ":" + (PlayerCharacter != nint.Zero ? "HasChar" : "NoChar");
    }

    internal void SetUploading(bool isUploading = true)
    {
        Logger.LogTrace("Setting {this} uploading {uploading}", this, isUploading);
        if (_charaHandler != null)
        {
            Mediator.Publish(new PlayerUploadingMessage(_charaHandler, isUploading));
        }
    }

    /// <summary>
    /// Disposes the handler and performs deferred cleanup to restore the player's original state.
    /// Heavy IPC calls are offloaded to background tasks to minimize impact on rendering.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        SetUploading(isUploading: false);
        var name = PlayerName;
        Logger.LogDebug("Disposing {name} ({user})", name, Pair);
        try
        {
            // Clear busy flag so deferred UI actions (e.g., pause) can proceed safely
            Pair.SetApplying(false);
            Guid applicationId = Guid.NewGuid();
            _applicationCancellationTokenSource?.CancelDispose();
            _applicationCancellationTokenSource = null;
            _downloadCancellationTokenSource?.CancelDispose();
            _downloadCancellationTokenSource = null;
            bool shouldUnapply = true;
            try
            {
                // release presence for this service; only unapply if no other services still hold leases
                var pc = _dalamudUtil.FindPlayerByNameHash(Pair.Ident);
                var applyKey = BuildApplyKey(pc.Name, null, Pair.UserData.UID);
                var serviceKey = BuildServiceKey(Pair.ApiUrlOverride ?? string.Empty, _downloadManager.ServerIndex ?? -1);
                shouldUnapply = _presence.Release(serviceKey, applyKey);
                // Defensive: if other services still have leases, do not unapply
                if (shouldUnapply && _presence.GetRefCount(applyKey) > 0)
                    shouldUnapply = false;
            }
            catch { }

            _downloadManager.Dispose();
            _charaHandler?.Dispose();
            _charaHandler = null;

            if (!string.IsNullOrEmpty(name))
            {
                Mediator.Publish(new EventMessage(new Event(name, Pair.UserData, nameof(PairHandler), EventSeverity.Informational, "Disposing User") { Server = (Pair.ApiUrlOverride).ToServerLabel() }));
            }

            if (_lifetime.ApplicationStopping.IsCancellationRequested) return;

            if (shouldUnapply && _dalamudUtil is { IsZoning: false, IsInCutscene: false } && !string.IsNullOrEmpty(name))
            {
                Logger.LogTrace("[{applicationId}] Restoring state for {name} ({OnlineUser})", applicationId, name, Pair.UserPair);
                // Defer heavy IPC cleanup to a background task to avoid blocking the game during draw/apply
                var cachedDataSnapshot = _cachedData?.DeepClone();
                var identSnapshot = Pair.Ident; // capture before pair mark-offline clears it
                var deferCompletely = Pair.ConsumeDeferCleanupOnce();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (deferCompletely)
                        {
                            // Safe Pause: skip immediate cleanup; wait for a longer idle window
                            var startSafe = DateTime.UtcNow;
                            while ((_dalamudUtil.IsAnythingDrawing || Pair.IsApplying) && DateTime.UtcNow - startSafe < TimeSpan.FromSeconds(8))
                                await Task.Delay(100).ConfigureAwait(false);
                        }

                        // wait for a short idle window to reduce contention with redraws
                        var start = DateTime.UtcNow;
                        while ((_dalamudUtil.IsAnythingDrawing || Pair.IsApplying) && DateTime.UtcNow - start < TimeSpan.FromSeconds(3))
                            await Task.Delay(50).ConfigureAwait(false);

                        Logger.LogDebug("[{applicationId}] Removing Temp Collection for {name} ({user}) [async]", applicationId, name, Pair.UserPair);
                        await _ipcManager.Penumbra.RemoveTemporaryCollectionAsync(Logger, applicationId, _penumbraCollection).ConfigureAwait(false);

                        if (!IsVisible)
                        {
                            Logger.LogDebug("[{applicationId}] Restoring Glamourer for {name} ({user}) [async]", applicationId, name, Pair.UserPair);
                            await _ipcManager.Glamourer.RevertByNameAsync(Logger, name, applicationId).ConfigureAwait(false);

                            // Also clear honorifics and force a Penumbra redraw when disposing an out-of-visibility user
                            try
                            {
                                var addr = _dalamudUtil.GetPlayerCharacterFromCachedTableByIdent(Pair.Ident);
                                if (addr != nint.Zero)
                                {
                                    Logger.LogDebug("[{applicationId}] Clearing Honorific and Redrawing {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
                                    await _ipcManager.Honorific.ClearTitleAsync(addr).ConfigureAwait(false);
                                    using var tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player, () => addr, isWatched: false).ConfigureAwait(false);
                                    await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, CancellationToken.None).ConfigureAwait(false);
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.LogTrace(ex, "[{applicationId}] Failed to clear honorific/redraw during dispose for {name}", applicationId, name);
                            }
                        }
                        else if (cachedDataSnapshot != null)
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                            Logger.LogInformation("[{applicationId}] CachedData(snapshot) exists: {contains}", applicationId, cachedDataSnapshot.FileReplacements.Any());
                            foreach (KeyValuePair<ObjectKind, List<FileReplacementData>> item in cachedDataSnapshot.FileReplacements)
                            {
                                try
                                {
                                    await RevertCustomizationDataAsync(item.Key, name, identSnapshot, applicationId, cts.Token).ConfigureAwait(false);
                                }
                                catch (InvalidOperationException ex)
                                {
                                    Logger.LogWarning(ex, "Failed disposing player (not present anymore?)");
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Async cleanup failed for {name}", name);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error on disposal of {name}", name);
        }
        finally
        {
            PlayerName = null;
            _cachedData = null;
            Logger.LogDebug("Disposing {name} complete", name);
        }
    }

    /// <summary>
    /// Applies customization data for a specific object kind by dispatching to the relevant IPC services.
    /// Includes Glamourer, Customize+, Heels, Honorific, Moodles, PetNames, and forced redraws.
    /// </summary>
    private async Task ApplyCustomizationDataAsync(Guid applicationId, KeyValuePair<ObjectKind, HashSet<PlayerChanges>> changes, CharacterData charaData, CancellationToken token)
    {
        if (PlayerCharacter == nint.Zero) return;
        var ptr = PlayerCharacter;

        var handler = changes.Key switch
        {
            ObjectKind.Player => _charaHandler!,
            ObjectKind.Companion => await _gameObjectHandlerFactory.Create(changes.Key, () => _dalamudUtil.GetCompanionPtr(ptr), isWatched: false).ConfigureAwait(false),
            ObjectKind.MinionOrMount => await _gameObjectHandlerFactory.Create(changes.Key, () => _dalamudUtil.GetMinionOrMountPtr(ptr), isWatched: false).ConfigureAwait(false),
            ObjectKind.Pet => await _gameObjectHandlerFactory.Create(changes.Key, () => _dalamudUtil.GetPetPtr(ptr), isWatched: false).ConfigureAwait(false),
            _ => throw new NotSupportedException("ObjectKind not supported: " + changes.Key)
        };

        try
        {
            if (handler.Address == nint.Zero)
            {
                return;
            }

            Logger.LogDebug("[{applicationId}] Applying Customization Data for {handler}", applicationId, handler);
            await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, handler, applicationId, 30000, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var change in changes.Value.OrderBy(p => (int)p))
            {
                Logger.LogDebug("[{applicationId}] Processing {change} for {handler}", applicationId, change, handler);
                switch (change)
                {
                    case PlayerChanges.Customize:
                        if (charaData.CustomizePlusData.TryGetValue(changes.Key, out var customizePlusData))
                        {
                            _customizeIds[changes.Key] = await _ipcManager.CustomizePlus.SetBodyScaleAsync(handler.Address, customizePlusData).ConfigureAwait(false);
                        }
                        else if (_customizeIds.TryGetValue(changes.Key, out var customizeId))
                        {
                            await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                            _customizeIds.Remove(changes.Key);
                        }
                        break;

                    case PlayerChanges.Heels:
                        await _ipcManager.Heels.SetOffsetForPlayerAsync(handler.Address, charaData.HeelsData).ConfigureAwait(false);
                        break;

                    case PlayerChanges.Honorific:
                        await _ipcManager.Honorific.SetTitleAsync(handler.Address, charaData.HonorificData).ConfigureAwait(false);
                        break;

                    case PlayerChanges.Glamourer:
                        if (charaData.GlamourerData.TryGetValue(changes.Key, out var glamourerData))
                        {
                            await _ipcManager.Glamourer.ApplyAllAsync(Logger, handler, glamourerData, applicationId, token).ConfigureAwait(false);
                        }
                        break;

                    case PlayerChanges.Moodles:
                        await _ipcManager.Moodles.SetStatusAsync(handler.Address, charaData.MoodlesData).ConfigureAwait(false);
                        break;

                    case PlayerChanges.PetNames:
                        await _ipcManager.PetNames.SetPlayerData(handler.Address, charaData.PetNamesData).ConfigureAwait(false);
                        break;

                    case PlayerChanges.ForcedRedraw:
                        await _ipcManager.Penumbra.RedrawAsync(Logger, handler, applicationId, token).ConfigureAwait(false);
                        break;

                    default:
                        break;
                }
                token.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            if (handler != _charaHandler) handler.Dispose();
        }
    }

    /// <summary>
    /// Computes whether downloads are required and triggers the async download/apply flow.
    /// </summary>
    private void DownloadAndApplyCharacter(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData)
    {
        if (!updatedData.Any())
        {
            Logger.LogDebug("[BASE-{appBase}] Nothing to update for {obj}", applicationBase, this);
            return;
        }

        var updateModdedPaths = updatedData.Values.Any(v => v.Any(p => p == PlayerChanges.ModFiles));
        var updateManip = updatedData.Values.Any(v => v.Any(p => p == PlayerChanges.ModManip));

        _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate() ?? new CancellationTokenSource();
        var downloadToken = _downloadCancellationTokenSource.Token;

        _ = DownloadAndApplyCharacterAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, downloadToken).ConfigureAwait(false);
    }

    private Task? _pairDownloadTask;

    /// <summary>
    /// Performs the download of missing assets (with integrity checks) and schedules the application task.
    /// Blocks overlapping applications and respects cancellation during zoning/combat.
    /// </summary>
    private async Task DownloadAndApplyCharacterAsync(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData,
        bool updateModdedPaths, bool updateManip, CancellationToken downloadToken)
    {
        Dictionary<(string GamePath, string? Hash), string> moddedPaths = [];

        if (updateModdedPaths)
        {
            int attempts = 0;
            List<FileReplacementData> toDownloadReplacements = TryCalculateModdedDictionary(applicationBase, charaData, out moddedPaths, downloadToken);

            while (toDownloadReplacements.Count > 0 && attempts++ <= 10 && !downloadToken.IsCancellationRequested)
            {
                if (_pairDownloadTask != null && !_pairDownloadTask.IsCompleted)
                {
                    Logger.LogDebug("[BASE-{appBase}] Finishing prior running download task for player {name}, {kind}", applicationBase, PlayerName, updatedData);
                    await _pairDownloadTask.ConfigureAwait(false);
                }

                // Snapshot handler to avoid race where _charaHandler becomes null mid-iteration
                var handlerSnapshot = _charaHandler;
                if (handlerSnapshot == null || handlerSnapshot.Address == nint.Zero)
                {
                    Logger.LogDebug("[BASE-{appBase}] Handler missing/invalid before download start; deferring application and caching data", applicationBase);
                    _cachedData = charaData;
                    _forceApplyMods = true;
                    return;
                }

                Logger.LogDebug("[BASE-{appBase}] Downloading missing files for player {name}, {kind}", applicationBase, PlayerName, updatedData);

                Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
                    $"Starting download for {toDownloadReplacements.Count} files") { Server = (Pair.ApiUrlOverride).ToServerLabel() }));
                var toDownloadFiles = await _downloadManager.InitiateDownloadList(handlerSnapshot, toDownloadReplacements, downloadToken).ConfigureAwait(false);

                if (!_playerPerformanceService.ComputeAndAutoPauseOnVRAMUsageThresholds(this, charaData, toDownloadFiles))
                {
                    _downloadManager.ClearDownload();
                    return;
                }

                // Coalesce downloads across services/handlers by the file-set signature to avoid redundant concurrent downloads
                var signature = string.Join(',', toDownloadReplacements.Select(r => r.Hash).OrderBy(h => h, StringComparer.Ordinal));
                _pairDownloadTask = _personDownloadCoordinator.RunCoalescedAsync(signature,
                    () => _downloadManager.DownloadFiles(handlerSnapshot, toDownloadReplacements, downloadToken));

                await _pairDownloadTask.ConfigureAwait(false);

                if (downloadToken.IsCancellationRequested)
                {
                    Logger.LogTrace("[BASE-{appBase}] Detected cancellation", applicationBase);
                    return;
                }

                toDownloadReplacements = TryCalculateModdedDictionary(applicationBase, charaData, out moddedPaths, downloadToken);

                if (toDownloadReplacements.TrueForAll(c => _downloadManager.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, c.Hash, StringComparison.Ordinal))))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), downloadToken).ConfigureAwait(false);
            }

            if (!await _playerPerformanceService.CheckBothThresholds(this, charaData).ConfigureAwait(false))
                return;
        }

        downloadToken.ThrowIfCancellationRequested();

        var appToken = _applicationCancellationTokenSource?.Token;
        while ((!_applicationTask?.IsCompleted ?? false)
               && !downloadToken.IsCancellationRequested
               && (!appToken?.IsCancellationRequested ?? false))
        {
            // block until current application is done
            Logger.LogDebug("[BASE-{appBase}] Waiting for current data application (Id: {id}) for player ({handler}) to finish", applicationBase, _applicationId, PlayerName);
            await Task.Delay(250).ConfigureAwait(false);
        }

        if (downloadToken.IsCancellationRequested || (appToken?.IsCancellationRequested ?? false)) return;

        _applicationCancellationTokenSource = _applicationCancellationTokenSource.CancelRecreate() ?? new CancellationTokenSource();
        var token = _applicationCancellationTokenSource.Token;

        // Acquire cross-service presence before applying so disconnects don’t revert if other services still hold leases
        try
        {
            var pc = _dalamudUtil.FindPlayerByNameHash(Pair.Ident);
            var applyKey = BuildApplyKey(pc.Name, null, Pair.UserData.UID);
            var serviceKey = BuildServiceKey(Pair.ApiUrlOverride ?? string.Empty, _downloadManager.ServerIndex ?? -1);
            _presence.Acquire(serviceKey, applyKey, charaData.DataHash.Value ?? string.Empty);
        }
        catch { }

        _applicationTask = ApplyCharacterDataAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip, moddedPaths, token);
    }

    /// <summary>
    /// Applies the prepared character data: assigns temporary collection, sets mods and manipulations,
    /// and applies customization per object kind while waiting for safe draw windows.
    /// </summary>
    private async Task ApplyCharacterDataAsync(Guid applicationBase, CharacterData charaData, Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData, bool updateModdedPaths, bool updateManip,
        Dictionary<(string GamePath, string? Hash), string> moddedPaths, CancellationToken token)
    {
        try
        {
            _applicationId = Guid.NewGuid();
            Logger.LogDebug("[BASE-{applicationId}] Starting application task for {this}: {appId}", applicationBase, this, _applicationId);

            var handlerSnapshot = _charaHandler;
            if (handlerSnapshot == null || handlerSnapshot.Address == nint.Zero)
            {
                // Character vanished between scheduling and apply start: cache and defer
                _forceApplyMods = true;
                _cachedData = charaData;
                Logger.LogDebug("[{applicationId}] Aborting apply early: handler/address invalid; will reapply on visibility", _applicationId);
                return;
            }

            Logger.LogDebug("[{applicationId}] Waiting for initial draw for for {handler}", _applicationId, handlerSnapshot);
            await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, handlerSnapshot, _applicationId, 30000, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            if (updateModdedPaths)
            {
                // ensure collection is set; GameObject access must occur on the framework thread
                var objIndex = await _dalamudUtil
                    .RunOnFrameworkThread(() => (int?)handlerSnapshot.GetGameObject()?.ObjectIndex)
                    .ConfigureAwait(false);
                if (objIndex == null)
                {
                    _forceApplyMods = true;
                    _cachedData = charaData;
                    Logger.LogDebug("[{applicationId}] GameObject null during apply; deferring", _applicationId);
                    return;
                }
                await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, objIndex.Value).ConfigureAwait(false);

                await _ipcManager.Penumbra.SetTemporaryModsAsync(Logger, _applicationId, _penumbraCollection,
                    moddedPaths.ToDictionary(k => k.Key.GamePath, k => k.Value, StringComparer.Ordinal)).ConfigureAwait(false);
                LastAppliedDataBytes = -1;
                foreach (var path in moddedPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Select(v => new FileInfo(v)).Where(p => p.Exists))
                {
                    if (LastAppliedDataBytes == -1) LastAppliedDataBytes = 0;

                    LastAppliedDataBytes += path.Length;
                }
            }

            if (updateManip)
            {
                await _ipcManager.Penumbra.SetManipulationDataAsync(Logger, _applicationId, _penumbraCollection, charaData.ManipulationData).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            foreach (var kind in updatedData)
            {
                await ApplyCustomizationDataAsync(_applicationId, kind, charaData, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }

            _cachedData = charaData;

            Logger.LogDebug("[{applicationId}] Application finished", _applicationId);
        }
        catch (Exception ex)
        {
            if (ex is AggregateException aggr && aggr.InnerExceptions.Any(e => e is ArgumentNullException))
            {
                IsVisible = false;
                _forceApplyMods = true;
                _cachedData = charaData;
                Logger.LogDebug("[{applicationId}] Cancelled, player turned null during application", _applicationId);
            }
            else
            {
                Logger.LogWarning(ex, "[{applicationId}] Cancelled", _applicationId);
            }
        }
        finally
        {
            // Mark pair as no longer applying so UI actions (pause/unpause) become available again
            Pair.SetApplying(false);
            Mediator.Publish(new RefreshUiMessage());
        }
    }

    /// <summary>
    /// Periodic update tied to the framework. Resolves initial name/ident, manages visibility transitions,
    /// and triggers reapplication on return to visibility.
    /// </summary>
    private void FrameworkUpdate()
    {
        if (string.IsNullOrEmpty(PlayerName))
        {
            var pc = _dalamudUtil.FindPlayerByNameHash(Pair.Ident);
            if (pc == default((string, nint))) return;
            Logger.LogDebug("One-Time Initializing {this}", this);
            Initialize(pc.Name);
            try { Mediator.Publish(new PlayerNameKnownMessage(Pair.UserData.UID, pc.Name)); } catch { }
            Logger.LogDebug("One-Time Initialized {this}", this);
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler), EventSeverity.Informational,
                $"Initializing User For Character {pc.Name}") { Server = (Pair.ApiUrlOverride).ToServerLabel() }));
        }

        if (_charaHandler?.Address != nint.Zero && !IsVisible)
        {
            Guid appData = Guid.NewGuid();
            IsVisible = true;
            if (_cachedData != null)
            {
                Logger.LogTrace("[BASE-{appBase}] {this} visibility changed, now: {visi}, cached data exists", appData, this, IsVisible);

                _ = Task.Run(() =>
                {
                    ApplyCharacterData(appData, _cachedData!, forceApplyCustomization: true);
                });
            }
            else
            {
                Logger.LogTrace("{this} visibility changed, now: {visi}, no cached data exists", this, IsVisible);
            }

            // On any return to visibility, proactively request the latest data from the network.
            // We still apply cached data immediately (if present) for fast visual restoration,
            // then a fresh payload (if newer) will arrive and re-apply on top. Gate by a small cooldown.
            try
            {
                var now = DateTime.UtcNow;
                if (now - _lastVisibilityDataRequestUtc > TimeSpan.FromSeconds(5))
                {
                    // Broadcast to ALL connected hubs (no ApiUrlOverride) so each service/server
                    // that knows this UID can respond with the freshest character data.
                    Mediator.Publish(new RequestUserDataForUidMessage(Pair.UserData));
                    Logger.LogDebug("[{appData}] Visibility regain: requested fresh data for {alias} ({uid}) via broadcast", appData, Pair.UserData.AliasOrUID, Pair.UserData.UID);
                    _lastVisibilityDataRequestUtc = now;
                }
            }
            catch { }
        }
        else if (_charaHandler?.Address == nint.Zero && IsVisible)
        {
            IsVisible = false;
            _charaHandler.Invalidate();
            // Do NOT cancel downloads on transient visibility loss.
            // Keeping ongoing downloads allows reuse of freshly fetched files if the player reappears shortly,
            // and avoids TaskCanceledException storms during high churn.
            // Cancellation still occurs on zone switches or explicit disposals where teardown is required.
            Logger.LogTrace("{this} visibility changed, now: {visi}", this, IsVisible);
        }
    }

    /// <summary>
    /// Initializes the handler by resolving the game object by ident first, then falling back to name.
    /// Also subscribes to plugin-ready messages to reapply persisted cosmetic state.
    /// </summary>
    private void Initialize(string name)
    {
        Logger.LogTrace("Initializing PairHandler for {alias}; name={name}, ident={ident}", Pair.UserData.AliasOrUID, name, Pair.Ident);
        PlayerName = name;
        // Resolve by Ident (hashed CID) first; if not found (service-specific ident mismatch), fall back to exact name match.
        _charaHandler = _gameObjectHandlerFactory.Create(ObjectKind.Player, () =>
        {
            var byIdent = _dalamudUtil.GetPlayerCharacterFromCachedTableByIdent(Pair.Ident);
            if (byIdent != nint.Zero)
            {
                Logger.LogTrace("Resolved player address by ident {ident}: {addr}", Pair.Ident, byIdent);
                return byIdent;
            }
            var byName = _dalamudUtil.GetPlayerCharacterFromCachedTableByName(name);
            Logger.LogTrace("Ident resolution failed for {ident}; name fallback {name} -> {addr}", Pair.Ident, name, byName);
            return byName;
        }, isWatched: false).GetAwaiter().GetResult();

        Logger.LogTrace("Finalize initialization for {alias}/{name}: resolved address {addr}", Pair.UserData.AliasOrUID, name, _charaHandler?.Address ?? nint.Zero);

        _serverConfigManager.AutoPopulateNoteForUid(Pair.UserData.UID, name);

        Mediator.Subscribe<HonorificReadyMessage>(this, async (_) =>
        {
            if (string.IsNullOrEmpty(_cachedData?.HonorificData)) return;
            Logger.LogTrace("Reapplying Honorific data for {this}", this);
            await _ipcManager.Honorific.SetTitleAsync(PlayerCharacter, _cachedData.HonorificData).ConfigureAwait(false);
        });

        Mediator.Subscribe<PetNamesReadyMessage>(this, async (_) =>
        {
            if (string.IsNullOrEmpty(_cachedData?.PetNamesData)) return;
            Logger.LogTrace("Reapplying Pet Names data for {this}", this);
            await _ipcManager.PetNames.SetPlayerData(PlayerCharacter, _cachedData.PetNamesData).ConfigureAwait(false);
        });

        try
        {
            var objIndex = _dalamudUtil.RunOnFrameworkThread(() => (int?)_charaHandler.GetGameObject()?.ObjectIndex).GetAwaiter().GetResult();
            if (objIndex != null)
            {
                _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, objIndex.Value).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // If we cannot get an object index right now (e.g., not on framework or object not ready), we'll assign on first apply.
        }
    }

    /// <summary>
    /// Reverts customization data for a specific object kind using IPC. Uses a strict name check to avoid
    /// reverting the wrong target and ensures a redraw to visually reset state.
    /// </summary>
    private async Task RevertCustomizationDataAsync(ObjectKind objectKind, string name, string ident, Guid applicationId, CancellationToken cancelToken)
    {
        nint address = _dalamudUtil.GetPlayerCharacterFromCachedTableByIdent(ident);
        if (address == nint.Zero && objectKind == ObjectKind.Player)
        {
            // Fallback: if we cannot resolve a live address, at least revert by name via Glamourer
            Logger.LogDebug("[{applicationId}] Could not resolve address for {alias}/{name}; reverting by name", applicationId, Pair.UserData.AliasOrUID, name);
            try
            {
                await _ipcManager.Glamourer.RevertByNameAsync(Logger, name, applicationId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[{applicationId}] RevertByName fallback failed for {name}", applicationId, name);
            }
            return;
        }
        if (address == nint.Zero) return;

        Logger.LogDebug("[{applicationId}] Reverting all Customization for {alias}/{name} {objectKind}", applicationId, Pair.UserData.AliasOrUID, name, objectKind);

        if (_customizeIds.TryGetValue(objectKind, out var customizeId))
        {
            _customizeIds.Remove(objectKind);
        }

        if (objectKind == ObjectKind.Player)
        {
            using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player, () => address, isWatched: false).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Customization and Equipment for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Heels for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Heels.RestoreOffsetForPlayerAsync(address).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring C+ for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Honorific for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Honorific.ClearTitleAsync(address).ConfigureAwait(false);
            Logger.LogDebug("[{applicationId}] Restoring Moodles for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.Moodles.RevertStatusAsync(address).ConfigureAwait(false);
            Logger.LogDebug("[{applicationId}] Restoring Pet Nicknames for {alias}/{name}", applicationId, Pair.UserData.AliasOrUID, name);
            await _ipcManager.PetNames.ClearPlayerData(address).ConfigureAwait(false);
            // Ensure a redraw so visual state fully resets
            await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
        }
        else if (objectKind == ObjectKind.MinionOrMount)
        {
            var minionOrMount = await _dalamudUtil.GetMinionOrMountAsync(address).ConfigureAwait(false);
            if (minionOrMount != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.MinionOrMount, () => minionOrMount, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
        else if (objectKind == ObjectKind.Pet)
        {
            var pet = await _dalamudUtil.GetPetAsync(address).ConfigureAwait(false);
            if (pet != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Pet, () => pet, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
        else if (objectKind == ObjectKind.Companion)
        {
            var companion = await _dalamudUtil.GetCompanionAsync(address).ConfigureAwait(false);
            if (companion != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Pet, () => companion, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Calculates the mapping of game paths to local files, verifies cache integrity against server-provided
    /// sizes (unless PlayerSync direct-downloads are in use), and returns the list of missing files to download.
    /// </summary>
    private List<FileReplacementData> TryCalculateModdedDictionary(Guid applicationBase, CharacterData charaData, out Dictionary<(string GamePath, string? Hash), string> moddedDictionary, CancellationToken token)
    {
        Stopwatch st = Stopwatch.StartNew();
        ConcurrentBag<FileReplacementData> missingFiles = [];
        moddedDictionary = [];
        ConcurrentDictionary<(string GamePath, string? Hash), string> outputDict = new();
        bool hasMigrationChanges = false;

        try
        {
            var replacementList = charaData.FileReplacements.SelectMany(k => k.Value.Where(v => string.IsNullOrEmpty(v.FileSwapPath))).ToList();

            // Server-side verification: ask the server for file info for all hashes in this batch.
            // If local cache exists but file size differs from RawSize reported by server, mark as missing to force re-download.
            Dictionary<string, DownloadFileDto> serverInfo = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                var allHashes = replacementList.Select(r => r.Hash).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                serverInfo = _downloadManager.GetServerFileInfoAsync(allHashes, token).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[BASE-{appBase}] Could not fetch server file info for verification, proceeding with local-only checks", applicationBase);
            }
            Parallel.ForEach(replacementList, new ParallelOptions()
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = 4
            },
            (item) =>
            {
                token.ThrowIfCancellationRequested();
                var fileCache = _fileDbManager.GetFileCacheByHash(item.Hash);
                if (fileCache != null)
                {
                    // Check if this is a PlayerSync server with direct download enabled
                    bool isPlayerSyncDirectDownload = _downloadManager.ServerIndex.HasValue && 
                        _serverConfigManager.GetServerByIndex(_downloadManager.ServerIndex.Value)?.UseDirectDownload == true;
                        
                    // verify against server RawSize if available, but skip verification for PlayerSync direct downloads
                    if (!isPlayerSyncDirectDownload && serverInfo.TryGetValue(item.Hash, out var info)
                        && info.RawSize > 0)
                    {
                        try
                        {
                            var fi = new FileInfo(fileCache.ResolvedFilepath);
                            if (!fi.Exists || fi.Length != info.RawSize)
                            {
                                Logger.LogInformation("[BASE-{appBase}] Local cache size mismatch for {hash}: local={local} bytes server={server} bytes; redownloading", applicationBase, item.Hash, fi.Exists ? fi.Length : -1, info.RawSize);
                                missingFiles.Add(item);
                                return; // skip mapping to outputDict so it will be downloaded
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogDebug(ex, "[BASE-{appBase}] Could not compare file size for {hash}, scheduling re-download", applicationBase, item.Hash);
                            missingFiles.Add(item);
                            return;
                        }
                    }
                    else if (isPlayerSyncDirectDownload)
                    {
                        // For PlayerSync direct downloads, just verify the file exists locally
                        try
                        {
                            var fi = new FileInfo(fileCache.ResolvedFilepath);
                            if (!fi.Exists)
                            {
                                Logger.LogInformation("[BASE-{appBase}] PlayerSync file {hash} missing locally, scheduling re-download", applicationBase, item.Hash);
                                missingFiles.Add(item);
                                return;
                            }
                            Logger.LogTrace("[BASE-{appBase}] PlayerSync file {hash} exists locally ({bytes} bytes), using cached version", applicationBase, item.Hash, fi.Length);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogDebug(ex, "[BASE-{appBase}] Could not verify PlayerSync file {hash}, scheduling re-download", applicationBase, item.Hash);
                            missingFiles.Add(item);
                            return;
                        }
                    }

                    if (string.IsNullOrEmpty(new FileInfo(fileCache.ResolvedFilepath).Extension))
                    {
                        hasMigrationChanges = true;
                        fileCache = _fileDbManager.MigrateFileHashToExtension(fileCache, item.GamePaths[0].Split(".")[^1]);
                    }

                    foreach (var gamePath in item.GamePaths)
                    {
                        outputDict[(gamePath, item.Hash)] = fileCache.ResolvedFilepath;
                    }
                }
                else
                {
                    Logger.LogTrace("Missing file: {hash}", item.Hash);
                    missingFiles.Add(item);
                }
            });

            moddedDictionary = outputDict.ToDictionary(k => k.Key, k => k.Value);

            foreach (var item in charaData.FileReplacements.SelectMany(k => k.Value.Where(v => !string.IsNullOrEmpty(v.FileSwapPath))).ToList())
            {
                foreach (var gamePath in item.GamePaths)
                {
                    Logger.LogTrace("[BASE-{appBase}] Adding file swap for {path}: {fileSwap}", applicationBase, gamePath, item.FileSwapPath);
                    moddedDictionary[(gamePath, null)] = item.FileSwapPath;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BASE-{appBase}] Something went wrong during calculation replacements", applicationBase);
        }
        if (hasMigrationChanges) _fileDbManager.WriteOutFullCsv();
        st.Stop();
        Logger.LogDebug("[BASE-{appBase}] ModdedPaths calculated in {time}ms, missing files: {count}, total files: {total}", applicationBase, st.ElapsedMilliseconds, missingFiles.Count, moddedDictionary.Keys.Count);
        return [.. missingFiles];
    }
}
