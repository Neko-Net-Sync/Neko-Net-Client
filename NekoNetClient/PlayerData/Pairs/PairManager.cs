/*
     Neko-Net Client — PlayerData.Pairs.PairManager
     ------------------------------------------------
     Purpose
     - Central registry for all known pairs (direct and group) within the client session.
     - Tracks cross-service presence by optionally scoping to an API URL override (service-scoped mode),
         and maintains pair lifecycles without disposing users that are still present on another service.
     - Provides lazy materialized views for: direct pairs, group pairs, and pairs with groups to power UI.
     - Reacts to mediator events (disconnects, cutscene end) to keep the on-screen state consistent and
         trigger safe reapplication of the last received character data when appropriate.

     Notes on behavior
     - When operating in service-scoped mode, the manager will not subscribe to global disconnection clear events.
         This ensures pairs that continue to exist on another hub/service remain intact and are not disposed.
     - Pair permissions are synchronized from server payloads on every update. When pause state changes,
         related profile data is cleared to avoid stale UI remnants.
     - The manager does not perform heavy apply work itself; instead it delegates to Pair objects which in turn
         coordinate PairHandler instances for downloads and in-game application. This keeps responsibilities focused.

     Threading and performance
     - Internally uses ConcurrentDictionaries keyed by domain DTOs with stable comparers to avoid locking.
     - Exposes precomputed lazy containers that are re-created on mutations to avoid repeated recomputation.
*/
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using NekoNet.API.Data;
using NekoNet.API.Data.Comparer;
using NekoNet.API.Data.Enum;
using NekoNet.API.Data.Extensions;
using NekoNet.API.Dto.Group;
using NekoNet.API.Dto.User;
using NekoNetClient.MareConfiguration;
using NekoNetClient.MareConfiguration.Models;
using NekoNetClient.PlayerData.Factories;
using NekoNetClient.Services.Events;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Utils;
using System.Collections.Concurrent;

namespace NekoNetClient.PlayerData.Pairs;

/// <summary>
/// Manages the set of all known pairs for the active client scope.
/// <para>
/// Responsibilities:
/// - Store and update <see cref="Pair"/> instances keyed by <see cref="UserData"/>,
/// - Track group membership and expose fast UI-friendly views,
/// - React to lifecycle events (disconnect, cutscene end) to clear or reapply state,
/// - Enforce cross-service semantics via optional API URL override to prevent unnecessary disposal.
/// </para>
/// <para>
/// This manager is intentionally lightweight: heavy operations such as download/apply pipelines are delegated to
/// the <see cref="Pair"/> and its <c>PairHandler</c>. The manager focuses on consistency, permissions propagation,
/// and minimal recomputation via lazily computed views.
/// </para>
/// </summary>
public sealed class PairManager : DisposableMediatorSubscriberBase
{
    private readonly ConcurrentDictionary<UserData, Pair> _allClientPairs = new(UserDataComparer.Instance);
    private readonly ConcurrentDictionary<GroupData, GroupFullInfoDto> _allGroups = new(GroupDataComparer.Instance);
    private readonly MareConfigService _configurationService;
    private readonly IContextMenu _dalamudContextMenu;
    private readonly PairFactory _pairFactory;
    private readonly bool _serviceScoped;
    private readonly string? _apiUrlOverride;
    private Lazy<List<Pair>> _directPairsInternal;
    private Lazy<Dictionary<GroupFullInfoDto, List<Pair>>> _groupPairsInternal;
    private Lazy<Dictionary<Pair, List<GroupFullInfoDto>>> _pairsWithGroupsInternal;

    public PairManager(ILogger<PairManager> logger, PairFactory pairFactory,
                MareConfigService configurationService, MareMediator mediator,
                IContextMenu dalamudContextMenu, string? apiUrlOverride = null, bool serviceScoped = false) : base(logger, mediator)
    {
        _pairFactory = pairFactory;
        _configurationService = configurationService;
        _dalamudContextMenu = dalamudContextMenu;
        _apiUrlOverride = apiUrlOverride;
        _serviceScoped = serviceScoped;
        if (!_serviceScoped)
            Mediator.Subscribe<DisconnectedMessage>(this, (_) => ClearPairs());
        Mediator.Subscribe<CutsceneEndMessage>(this, (_) => ReapplyPairData());
        _directPairsInternal = DirectPairsLazy();
        _groupPairsInternal = GroupPairsLazy();
        _pairsWithGroupsInternal = PairsWithGroupsLazy();

        _dalamudContextMenu.OnMenuOpened += DalamudContextMenuOnOnOpenGameObjectContextMenu;
    }

    /// <summary>
    /// Gets the list of all directly paired users (bidirectional or one-sided), excluding group-only users.
    /// </summary>
    public List<Pair> DirectPairs => _directPairsInternal.Value;

    /// <summary>
    /// Gets the mapping of group metadata to its current member pairs.
    /// </summary>
    public Dictionary<GroupFullInfoDto, List<Pair>> GroupPairs => _groupPairsInternal.Value;
    /// <summary>
    /// Gets the current known groups keyed by <see cref="GroupData"/>.
    /// </summary>
    public Dictionary<GroupData, GroupFullInfoDto> Groups => _allGroups.ToDictionary(k => k.Key, k => k.Value);
    /// <summary>
    /// Gets the last pair instance that was added (used by some UI flows for focus/selection).
    /// </summary>
    public Pair? LastAddedUser { get; internal set; }
    /// <summary>
    /// Gets a reverse mapping for UI: a pair to the list of groups it currently belongs to.
    /// </summary>
    public Dictionary<Pair, List<GroupFullInfoDto>> PairsWithGroups => _pairsWithGroupsInternal.Value;

    /// <summary>
    /// Registers a group in the local index. If the group already exists, it is refreshed with the provided data.
    /// </summary>
    /// <param name="dto">The full group info received from the server.</param>
    public void AddGroup(GroupFullInfoDto dto)
    {
        _allGroups[dto.Group] = dto;
        RecreateLazy();
    }

    /// <summary>
    /// Registers or updates a group pair membership for a user and ensures the pair exists.
    /// </summary>
    /// <param name="dto">The group pair information payload.</param>
    public void AddGroupPair(GroupPairFullInfoDto dto)
    {
        if (!_allClientPairs.ContainsKey(dto.User))
        {
            var up = new UserFullPairDto(dto.User, IndividualPairStatus.None, [dto.Group.GID], dto.SelfToOtherPermissions, dto.OtherToSelfPermissions);
            _allClientPairs[dto.User] = _apiUrlOverride == null ? _pairFactory.Create(up) : _pairFactory.CreateForService(up, _apiUrlOverride);
        }
        else _allClientPairs[dto.User].UserPair.Groups.Add(dto.GID);
        RecreateLazy();
    }

    /// <summary>
    /// Retrieves a pair by UID if present.
    /// </summary>
    /// <param name="uid">The user UID.</param>
    /// <returns>The <see cref="Pair"/> instance or null if not found.</returns>
    public Pair? GetPairByUID(string uid)
    {
        var existingPair = _allClientPairs.FirstOrDefault(f => f.Key.UID == uid);
        if (!Equals(existingPair, default(KeyValuePair<UserData, Pair>)))
        {
            return existingPair.Value;
        }

        return null;
    }

    /// <summary>
    /// Adds or updates a direct pair using the full payload from the server.
    /// Always synchronizes permissions and, if the pair is not paused, triggers application of last received data.
    /// </summary>
    public void AddUserPair(UserFullPairDto dto)
    {
        if (!_allClientPairs.ContainsKey(dto.User))
        {
            _allClientPairs[dto.User] = _apiUrlOverride == null ? _pairFactory.Create(dto) : _pairFactory.CreateForService(dto, _apiUrlOverride);
        }
        else
        {
            _allClientPairs[dto.User].UserPair.IndividualPairStatus = dto.IndividualPairStatus;
        }

        // Always sync permissions from server payload
        _allClientPairs[dto.User].UserPair.OwnPermissions = dto.OwnPermissions;
        _allClientPairs[dto.User].UserPair.OtherPermissions = dto.OtherPermissions;

        // Only apply last data if not paused
        if (!_allClientPairs[dto.User].IsPaused)
            _allClientPairs[dto.User].ApplyLastReceivedData();

        RecreateLazy();
    }

    /// <summary>
    /// Adds or updates a direct pair using a minimal payload, optionally tagging the newly added user
    /// as the last-added for UI purposes. Permissions are synchronized and, if not paused, last data is applied.
    /// </summary>
    public void AddUserPair(UserPairDto dto, bool addToLastAddedUser = true)
    {
        if (!_allClientPairs.ContainsKey(dto.User))
        {
            _allClientPairs[dto.User] = _apiUrlOverride == null ? _pairFactory.Create(dto) : _pairFactory.CreateForService(dto, _apiUrlOverride);
        }
        else
        {
            addToLastAddedUser = false;
        }

        _allClientPairs[dto.User].UserPair.IndividualPairStatus = dto.IndividualPairStatus;
        _allClientPairs[dto.User].UserPair.OwnPermissions = dto.OwnPermissions;
        _allClientPairs[dto.User].UserPair.OtherPermissions = dto.OtherPermissions;
        if (addToLastAddedUser)
            LastAddedUser = _allClientPairs[dto.User];
        if (!_allClientPairs[dto.User].IsPaused)
            _allClientPairs[dto.User].ApplyLastReceivedData();
        RecreateLazy();
    }

    /// <summary>
    /// Clears all pair and group state. Disposes pair instances and rebuilds all lazy views.
    /// In non-service-scoped managers this is typically called on disconnect.
    /// </summary>
    public void ClearPairs()
    {
        Logger.LogDebug("Clearing all Pairs");
        DisposePairs();
        _allClientPairs.Clear();
        _allGroups.Clear();
        RecreateLazy();
    }

    /// <summary>
    /// Returns all user pairs that are currently online (have a known player name hash).
    /// </summary>
    public List<Pair> GetOnlineUserPairs() => _allClientPairs.Where(p => !string.IsNullOrEmpty(p.Value.GetPlayerNameHash())).Select(p => p.Value).ToList();

    /// <summary>
    /// Gets the count of pairs currently visible on screen.
    /// </summary>
    public int GetVisibleUserCount() => _allClientPairs.Count(p => p.Value.IsVisible);

    /// <summary>
    /// Returns all users that are currently visible.
    /// </summary>
    public List<UserData> GetVisibleUsers() => [.. _allClientPairs.Where(p => p.Value.IsVisible).Select(p => p.Key)];

    /// <summary>
    /// Marks the provided user as offline and publishes a profile clear message.
    /// </summary>
    public void MarkPairOffline(UserData user)
    {
        if (_allClientPairs.TryGetValue(user, out var pair))
        {
            Mediator.Publish(new ClearProfileDataMessage(pair.UserData));
            pair.MarkOffline();
        }

        RecreateLazy();
    }

    /// <summary>
    /// Selectively removes pairs based on the provided predicate. This is used to remove users that
    /// are no longer present on a specific service while keeping those that still exist elsewhere.
    /// </summary>
    public void SelectiveClear(Func<UserData, bool> shouldRemove)
    {
        foreach (var item in _allClientPairs.ToList())
        {
            if (!shouldRemove(item.Key)) continue;
            item.Value.MarkOffline();
            _allClientPairs.TryRemove(item.Key, out _);
        }
        RecreateLazy();
    }

    /// <summary>
    /// Marks a pair as online using the provided identification payload and optionally shows a notification.
    /// If the cached player already exists, the method returns early to avoid duplicate handlers.
    /// </summary>
    public void MarkPairOnline(OnlineUserIdentDto dto, bool sendNotif = true)
    {
        if (!_allClientPairs.ContainsKey(dto.User)) throw new InvalidOperationException("No user found for " + dto);

        Mediator.Publish(new ClearProfileDataMessage(dto.User));

        var pair = _allClientPairs[dto.User];
        if (pair.HasCachedPlayer)
        {
            RecreateLazy();
            return;
        }

        if (sendNotif && _configurationService.Current.ShowOnlineNotifications
            && (_configurationService.Current.ShowOnlineNotificationsOnlyForIndividualPairs && pair.IsDirectlyPaired && !pair.IsOneSidedPair
            || !_configurationService.Current.ShowOnlineNotificationsOnlyForIndividualPairs)
            && (_configurationService.Current.ShowOnlineNotificationsOnlyForNamedPairs && !string.IsNullOrEmpty(pair.GetNote())
            || !_configurationService.Current.ShowOnlineNotificationsOnlyForNamedPairs))
        {
            string? note = pair.GetNote();
            var msg = !string.IsNullOrEmpty(note)
                ? $"{note} ({pair.UserData.AliasOrUID}) is now online"
                : $"{pair.UserData.AliasOrUID} is now online";
            Mediator.Publish(new NotificationMessage("User online", msg, NotificationType.Info, TimeSpan.FromSeconds(5)));
        }

        pair.CreateCachedPlayer(dto);

        RecreateLazy();
    }

    /// <summary>
    /// Receives the character data for a user and either applies it immediately or stores it if the pair is paused.
    /// </summary>
    public void ReceiveCharaData(OnlineUserCharaDataDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User, out var pair)) throw new InvalidOperationException("No user found for " + dto.User);

        Mediator.Publish(new EventMessage(new Event(pair.UserData, nameof(PairManager), EventSeverity.Informational, "Received Character Data")
        { Server = VariousExtensions.ToServerLabel(pair.ApiUrlOverride) }));
        if (pair.IsPaused)
        {
            // Cache only; do not attempt to apply while paused
            pair.LastReceivedCharacterData = dto.CharaData;
            return;
        }
        _allClientPairs[dto.User].ApplyData(dto);
    }

    /// <summary>
    /// Removes a group entirely from the manager and updates all pairs. Pairs with no remaining connections
    /// (no groups and no direct relation) are marked offline and removed.
    /// </summary>
    public void RemoveGroup(GroupData data)
    {
        _allGroups.TryRemove(data, out _);

        foreach (var item in _allClientPairs.ToList())
        {
            item.Value.UserPair.Groups.Remove(data.GID);

            if (!item.Value.HasAnyConnection())
            {
                item.Value.MarkOffline();
                _allClientPairs.TryRemove(item.Key, out _);
            }
        }

        RecreateLazy();
    }

    /// <summary>
    /// Removes a specific user from a group and prunes the pair if it no longer has any connections.
    /// </summary>
    public void RemoveGroupPair(GroupPairDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            pair.UserPair.Groups.Remove(dto.Group.GID);

            if (!pair.HasAnyConnection())
            {
                pair.MarkOffline();
                _allClientPairs.TryRemove(dto.User, out _);
            }
        }

        RecreateLazy();
    }

    /// <summary>
    /// Removes a direct pair relationship from a user and prunes it if it no longer has any connections.
    /// </summary>
    public void RemoveUserPair(UserDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            pair.UserPair.IndividualPairStatus = IndividualPairStatus.None;

            if (!pair.HasAnyConnection())
            {
                pair.MarkOffline();
                _allClientPairs.TryRemove(dto.User, out _);
            }
        }

        RecreateLazy();
    }

    /// <summary>
    /// Updates the high-level metadata for a group.
    /// </summary>
    public void SetGroupInfo(GroupInfoDto dto)
    {
        _allGroups[dto.Group].Group = dto.Group;
        _allGroups[dto.Group].Owner = dto.Owner;
        _allGroups[dto.Group].GroupPermissions = dto.GroupPermissions;

        RecreateLazy();
    }

    /// <summary>
    /// Updates the permissions that others have towards self for the specified user and re-applies data
    /// if the pair is not paused. Also emits trace logging of the new state.
    /// </summary>
    public void UpdatePairPermissions(UserPermissionsDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            throw new InvalidOperationException("No such pair for " + dto);
        }

        if (pair.UserPair == null) throw new InvalidOperationException("No direct pair for " + dto);

        if (pair.UserPair.OtherPermissions.IsPaused() != dto.Permissions.IsPaused())
        {
            Mediator.Publish(new ClearProfileDataMessage(dto.User));
        }

        pair.UserPair.OtherPermissions = dto.Permissions;

        Logger.LogTrace("Paused: {paused}, Anims: {anims}, Sounds: {sounds}, VFX: {vfx}",
            pair.UserPair.OtherPermissions.IsPaused(),
            pair.UserPair.OtherPermissions.IsDisableAnimations(),
            pair.UserPair.OtherPermissions.IsDisableSounds(),
            pair.UserPair.OtherPermissions.IsDisableVFX());

        if (!pair.IsPaused)
            pair.ApplyLastReceivedData();

        RecreateLazy();
    }

    /// <summary>
    /// Updates the permissions self has towards the specified user and re-applies data if not paused.
    /// Clears profile data when toggling pause to avoid stale UI renderings.
    /// </summary>
    public void UpdateSelfPairPermissions(UserPermissionsDto dto)
    {
        if (!_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            throw new InvalidOperationException("No such pair for " + dto);
        }

        if (pair.UserPair.OwnPermissions.IsPaused() != dto.Permissions.IsPaused())
        {
            Mediator.Publish(new ClearProfileDataMessage(dto.User));
        }

        pair.UserPair.OwnPermissions = dto.Permissions;

        Logger.LogTrace("Paused: {paused}, Anims: {anims}, Sounds: {sounds}, VFX: {vfx}",
            pair.UserPair.OwnPermissions.IsPaused(),
            pair.UserPair.OwnPermissions.IsDisableAnimations(),
            pair.UserPair.OwnPermissions.IsDisableSounds(),
            pair.UserPair.OwnPermissions.IsDisableVFX());

        if (!pair.IsPaused)
            pair.ApplyLastReceivedData();

        RecreateLazy();
    }

    internal void ReceiveUploadStatus(UserDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var existingPair) && existingPair.IsVisible)
        {
            existingPair.SetIsUploading();
        }
    }

    internal void SetGroupPairStatusInfo(GroupPairUserInfoDto dto)
    {
        _allGroups[dto.Group].GroupPairUserInfos[dto.UID] = dto.GroupUserInfo;
        RecreateLazy();
    }

    internal void SetGroupPermissions(GroupPermissionDto dto)
    {
        _allGroups[dto.Group].GroupPermissions = dto.Permissions;
        RecreateLazy();
    }

    internal void SetGroupStatusInfo(GroupPairUserInfoDto dto)
    {
        _allGroups[dto.Group].GroupUserInfo = dto.GroupUserInfo;
        RecreateLazy();
    }

    internal void UpdateGroupPairPermissions(GroupPairUserPermissionDto dto)
    {
        _allGroups[dto.Group].GroupUserPermissions = dto.GroupPairPermissions;
        RecreateLazy();
    }

    internal void UpdateIndividualPairStatus(UserIndividualPairStatusDto dto)
    {
        if (_allClientPairs.TryGetValue(dto.User, out var pair))
        {
            pair.UserPair.IndividualPairStatus = dto.IndividualPairStatus;
            RecreateLazy();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        _dalamudContextMenu.OnMenuOpened -= DalamudContextMenuOnOnOpenGameObjectContextMenu;

        DisposePairs();
    }

    private void DalamudContextMenuOnOnOpenGameObjectContextMenu(Dalamud.Game.Gui.ContextMenu.IMenuOpenedArgs args)
    {
        if (args.MenuType == Dalamud.Game.Gui.ContextMenu.ContextMenuType.Inventory) return;
        if (!_configurationService.Current.EnableRightClickMenus) return;

        foreach (var pair in _allClientPairs.Where((p => p.Value.IsVisible)))
        {
            pair.Value.AddContextMenu(args);
        }
    }

    private Lazy<List<Pair>> DirectPairsLazy() => new(() => _allClientPairs.Select(k => k.Value)
        .Where(k => k.IndividualPairStatus != IndividualPairStatus.None).ToList());

    private void DisposePairs()
    {
        Logger.LogDebug("Disposing all Pairs");
        Parallel.ForEach(_allClientPairs, item =>
        {
            item.Value.MarkOffline(wait: false);
        });

        RecreateLazy();
    }

    private Lazy<Dictionary<GroupFullInfoDto, List<Pair>>> GroupPairsLazy()
    {
        return new Lazy<Dictionary<GroupFullInfoDto, List<Pair>>>(() =>
        {
            Dictionary<GroupFullInfoDto, List<Pair>> outDict = [];
            foreach (var group in _allGroups)
            {
                outDict[group.Value] = _allClientPairs.Select(p => p.Value).Where(p => p.UserPair.Groups.Exists(g => GroupDataComparer.Instance.Equals(group.Key, new(g)))).ToList();
            }
            return outDict;
        });
    }

    private Lazy<Dictionary<Pair, List<GroupFullInfoDto>>> PairsWithGroupsLazy()
    {
        return new Lazy<Dictionary<Pair, List<GroupFullInfoDto>>>(() =>
        {
            Dictionary<Pair, List<GroupFullInfoDto>> outDict = [];

            foreach (var pair in _allClientPairs.Select(k => k.Value))
            {
                outDict[pair] = _allGroups.Where(k => pair.UserPair.Groups.Contains(k.Key.GID, StringComparer.Ordinal)).Select(k => k.Value).ToList();
            }

            return outDict;
        });
    }

    private void ReapplyPairData()
    {
        foreach (var pair in _allClientPairs.Select(k => k.Value))
        {
            pair.ApplyLastReceivedData(forced: true);
        }
    }

    private void RecreateLazy()
    {
        _directPairsInternal = DirectPairsLazy();
        _groupPairsInternal = GroupPairsLazy();
        _pairsWithGroupsInternal = PairsWithGroupsLazy();
        Mediator.Publish(new RefreshUiMessage());
    }
}
