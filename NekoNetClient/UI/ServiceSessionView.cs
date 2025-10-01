/*
     Neko-Net Client — UI.ServiceSessionView
     --------------------------------------
     Purpose
     - Lightweight, reusable view that renders a session (pairs + folders) for a specific service.
         Shows connection state, shard/CDN/last-push info, and provides filtering per service.
*/
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using NekoNet.API.Dto.Group;
using NekoNet.API.Dto.User;
using NekoNet.API.Data;
using NekoNet.API.Data.Enum;
using NekoNet.API.Data.Extensions;
using NekoNetClient.MareConfiguration;
using NekoNetClient.PlayerData.Pairs;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.UI.Components;
using NekoNetClient.UI.Handlers;
using NekoNetClient.WebAPI.SignalR;
using System.Collections.Immutable;
using System.Numerics;

namespace NekoNetClient.UI;

/// <summary>
/// Lightweight, reusable view that renders a session (pairs + folders) for a specific service.
/// </summary>
internal sealed class ServiceSessionView
{
    private readonly ILogger _log;
    private readonly UiSharedService _ui;
    private readonly MareConfigService _cfg;
    private readonly MultiHubManager _multi;
    private readonly ServerConfigurationManager _servers;
    private readonly DrawEntityFactory _drawFactory;

    // simple per-service filter state
    private readonly Dictionary<SyncService, string> _filters = new();

    // Cross Sync actions (configured servers) — lightweight per-index UI state
    private readonly Dictionary<int, string> _pairToAddByServer = new();
    private readonly Dictionary<int, bool> _showJoinModalByServer = new();
    private readonly Dictionary<int, string> _joinShellIdByServer = new();
    private readonly Dictionary<int, string> _joinShellPwByServer = new();
    private readonly Dictionary<int, string> _joinShellPrevPwByServer = new();
    private readonly Dictionary<int, GroupJoinInfoDto?> _joinInfoByServer = new();
    private readonly Dictionary<int, bool> _joinAttemptFailedByServer = new();

    public ServiceSessionView(
        ILogger log,
        UiSharedService ui,
        MareConfigService cfg,
        MultiHubManager multi,
        ServerConfigurationManager servers,
        DrawEntityFactory drawFactory)
    {
        _log = log;
        _ui = ui;
        _cfg = cfg;
        _multi = multi;
        _servers = servers;
        _drawFactory = drawFactory;
    }

    private static string ServiceLabel(SyncService svc) => svc switch
    {
        SyncService.NekoNet => "NekoNet",
        SyncService.Lightless => "Lightless",
        SyncService.TeraSync => "TeraSync",
        _ => svc.ToString()
    };






    public void DrawServiceHeader(SyncService svc)
    {
        var hubState = _multi.GetState(svc);
        var buttonSize = _ui.GetIconButtonSize(FontAwesomeIcon.Link);

        var online = 0;
        string shard = string.Empty;

        try
        {
            var (onlineResult, shardResult) = _multi.GetServiceOnlineAsync(svc, CancellationToken.None).GetAwaiter().GetResult();
            online = onlineResult ?? 0;
            shard = shardResult ?? string.Empty;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to get online info for {svc}", svc);
        }

        var onlineStr = online.ToString();
        var userSize = ImGui.CalcTextSize(onlineStr);
        var textSize = ImGui.CalcTextSize("Users Online");
        var shardText = string.IsNullOrEmpty(shard) ? string.Empty : $"Shard: {shard}";
        var shardSize = ImGui.CalcTextSize(shardText);
        var cdnHost = _multi.GetServiceCdnHost(svc) ?? "-";
        var lastPush = _multi.GetServiceLastPushUtc(svc);
        var lastPushText = lastPush.HasValue ? ($"Last push: {FormatAgo(lastPush.Value)}") : "Last push: -";
        var cdnText = $"CDN: {cdnHost}";
        var cdnSize = ImGui.CalcTextSize(cdnText);
        var pushSize = ImGui.CalcTextSize(lastPushText);

        ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth()) / 2 - (userSize.X + textSize.X) / 2 - ImGui.GetStyle().ItemSpacing.X / 2);
        if (hubState == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
        {
            if (!string.IsNullOrEmpty(shardText)) ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ImGuiColors.ParsedGreen, onlineStr);
            ImGui.SameLine();
            if (!string.IsNullOrEmpty(shardText)) ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Users Online");
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ImGuiColors.DalamudRed, "Not connected");
        }

        if (!string.IsNullOrEmpty(shardText))
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y);
            ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth()) / 2 - shardSize.X / 2);
            ImGui.TextUnformatted(shardText);
        }

        // CDN + Last push lines, centered
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y);
        ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth()) / 2 - cdnSize.X / 2);
        ImGui.TextColored(ImGuiColors.DalamudGrey, cdnText);
        ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth()) / 2 - pushSize.X / 2);
        ImGui.TextColored(ImGuiColors.DalamudGrey, lastPushText);

        // Centered UID (click to copy) for this service
        try
        {
            var router = new ServiceApiActionRouter(_multi, svc);
            var uid = router.UID;
            if (!string.IsNullOrWhiteSpace(uid))
            {
                using (_ui.UidFont.Push())
                {
                    var uidSize = ImGui.CalcTextSize(uid);
                    ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth()) / 2 - uidSize.X / 2);
                    ImGui.TextColored(ImGuiColors.ParsedGreen, uid);
                }
                if (ImGui.IsItemClicked()) ImGui.SetClipboardText(uid);
                UiSharedService.AttachToolTip("Click to copy");
            }
        }
        catch { }

        var connected = hubState is Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected or Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connecting or Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Reconnecting;
        var color = UiSharedService.GetBoolColor(!connected);
        var icon = connected ? FontAwesomeIcon.Unlink : FontAwesomeIcon.Link;
        ImGui.SameLine(ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth() - buttonSize.X);
        if (!string.IsNullOrEmpty(shardText))
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ((userSize.Y + textSize.Y) / 2 + shardSize.Y) / 2 - ImGui.GetStyle().ItemSpacing.Y + buttonSize.Y / 2);
        }
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            if (_ui.IconButton(icon))
            {
                try
                {
                    if (connected)
                        _ = _multi.DisconnectAsync(svc);
                    else
                        _ = _multi.ConnectAsync(svc);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to toggle {svc}", svc);
                }
            }
        }
        UiSharedService.AttachToolTip((connected ? "Disconnect" : "Connect") + " " + ServiceLabel(svc));
    }

    public void Draw(SyncService svc)
    {
        using (ImRaii.PushId("svcview-" + svc))
        {
            // Header
            DrawServiceHeader(svc);
            ImGui.Separator();

            // small filter row
            var filterKey = svc;
            if (!_filters.TryGetValue(filterKey, out var filter)) filter = string.Empty;
            var buttonSize = _ui.GetIconTextButtonSize(FontAwesomeIcon.Ban, "Clear");
            ImGui.SetNextItemWidth(UiSharedService.GetWindowContentRegionWidth() - buttonSize - ImGui.GetStyle().ItemSpacing.X);
            if (ImGui.InputTextWithHint("##svcfilter", "Filter for UID/notes", ref filter, 255))
            {
                _filters[filterKey] = filter;
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(string.IsNullOrEmpty(filter)))
            {
                if (_ui.IconTextButton(FontAwesomeIcon.Ban, "Clear"))
                {
                    _filters[filterKey] = string.Empty;
                    filter = string.Empty;
                }
            }

            // Draw pairs list in the same grouping logic as main view
            var pm = _multi.GetPairManagerForService(svc);
            // Seed from cached hub data if needed (throttled)
            TrySeedFromCachesThrottled(svc, pm);
            var apiUrl = ResolveApiUrlForService(svc);
            var tagHandler = new TagHandler(_servers, apiUrl);
            var router = new ServiceApiActionRouter(_multi, svc);

            var folders = BuildFolders(pm, tagHandler, router, filter);

            var availableY = (ImGui.GetWindowContentRegionMax().Y - ImGui.GetWindowContentRegionMin().Y
                + ImGui.GetTextLineHeight() - ImGui.GetStyle().WindowPadding.Y - ImGui.GetStyle().WindowBorderSize)
                - ImGui.GetCursorPosY();
            if (availableY < 1) availableY = 1;
            ImGui.BeginChild($"list-{svc}", new Vector2(UiSharedService.GetWindowContentRegionWidth(), availableY), border: false);
            foreach (var folder in folders)
            {
                folder.Draw();
            }
            ImGui.EndChild();
        }
    }

    private void TrySeedFromCachesThrottled(SyncService svc, PairManager pm)
    {
        var hubState = _multi.GetState(svc);
        
        if (hubState != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
        {
            return; // don't seed when disconnected; UI should show empty
        }
            
        TrySeedFromCaches(svc, pm);
    }

    private void TrySeedFromCaches(SyncService svc, PairManager pm)
    {
        try
        {
            // Push groups and members from cache
            var groups = _multi.GetServiceGroupInfos(svc);
            if (groups != null && groups.Count > 0)
            {
                foreach (var g in groups)
                {
                    try { pm.AddGroup(g); } catch { }
                    foreach (var uid in g.GroupPairUserInfos.Keys)
                    {
                        try
                        {
                            if (pm.GetPairByUID(uid) == null)
                            {
                                var minimal = new UserFullPairDto(new UserData(uid, string.Empty), IndividualPairStatus.None,
                                    new List<string> { g.Group.GID }, UserPermissions.NoneSet, UserPermissions.NoneSet);
                                pm.AddUserPair(minimal);
                            }
                        }
                        catch { }
                    }
                }
            }

            // Push online pairs from cache
            var online = _multi.GetServiceOnlinePairs(svc);
            if (online != null && online.Count > 0)
            {
                foreach (var o in online)
                {
                    try
                    {
                        if (pm.GetPairByUID(o.User.UID) == null)
                        {
                            var minimal = new UserFullPairDto(o.User, IndividualPairStatus.None, new List<string>(), UserPermissions.NoneSet, UserPermissions.NoneSet);
                            pm.AddUserPair(minimal);
                        }
                        pm.MarkPairOnline(o, sendNotif: false);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "TrySeedFromCaches failed for {svc}", svc);
        }
    }

    private void TrySeedFromConfiguredCachesThrottled(int serverIndex, PairManager pm)
    {
        var hubState = _multi.GetConfiguredState(serverIndex);
        
        if (hubState != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
        {
            return; // don't seed when disconnected; UI should show empty
        }
            
        TrySeedFromConfiguredCaches(serverIndex, pm);
    }

    private void TrySeedFromConfiguredCaches(int serverIndex, PairManager pm)
    {
        try
        {
            // Note: Configured servers may not have cached group/online info methods yet
            // This is primarily for throttling pair manager operations for now
            _log.LogTrace("Seeded configured server {idx} pair manager", serverIndex);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "TrySeedFromConfiguredCaches failed for server {idx}", serverIndex);
        }
    }

    private string ResolveApiUrlForService(SyncService svc)
    {
        var idx = _multi.GetServerIndexForService(svc);
        if (idx.HasValue)
        {
            try { return _servers.GetServerByIndex(idx.Value).ServerUri; } catch { }
        }
        try
        {
            // fallback to base URL without path
            var url = _multi.GetResolvedUrl(svc);
            var u = new Uri(url);
            return new UriBuilder(u.Scheme, u.Host, u.Port).Uri.ToString();
        }
        catch { return _servers.CurrentApiUrl; }
    }

    private List<IDrawFolder> BuildFolders(PairManager pm, TagHandler tagHandler, IApiActionRouter router, string filter)
    {
        List<IDrawFolder> drawFolders = [];

        var allPairs = pm.PairsWithGroups.ToDictionary(k => k.Key, k => k.Value);
        var filteredPairs = allPairs
            .Where(p =>
            {
                if (string.IsNullOrEmpty(filter)) return true;
                return p.Key.UserData.AliasOrUID.Contains(filter, StringComparison.OrdinalIgnoreCase)
                       || (p.Key.GetNote()?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                       || (p.Key.PlayerName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
            })
            .ToDictionary(k => k.Key, k => k.Value);

        string? AlphabeticalSort(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => (_cfg.Current.ShowCharacterNameInsteadOfNotesForVisible && !string.IsNullOrEmpty(u.Key.PlayerName)
                    ? (_cfg.Current.PreferNotesOverNamesForVisible ? u.Key.GetNote() : u.Key.PlayerName)
                    : (u.Key.GetNote() ?? u.Key.UserData.AliasOrUID));
        bool FilterOnlineOrPausedSelf(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => (u.Key.IsOnline || (!_cfg.Current.ShowOfflineUsersSeparately)
                    || u.Key.UserPair.OwnPermissions.IsPaused());
        Dictionary<Pair, List<GroupFullInfoDto>> BasicSortedDictionary(IEnumerable<KeyValuePair<Pair, List<GroupFullInfoDto>>> u)
            => u.OrderByDescending(u => u.Key.IsOnline)
                .ThenByDescending(u => u.Key.IsVisible)
                .ThenBy(AlphabeticalSort, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(u => u.Key, u => u.Value);
        ImmutableList<Pair> ImmutablePairList(IEnumerable<KeyValuePair<Pair, List<GroupFullInfoDto>>> u)
            => u.Select(k => k.Key).ToImmutableList();
        bool FilterVisibleUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => u.Key.IsVisible
                && (_cfg.Current.ShowSyncshellUsersInVisible || !(!_cfg.Current.ShowSyncshellUsersInVisible && !u.Key.IsDirectlyPaired));
        bool FilterTagusers(KeyValuePair<Pair, List<GroupFullInfoDto>> u, string tag)
            => u.Key.IsDirectlyPaired && !u.Key.IsOneSidedPair && tagHandler.HasTag(u.Key.UserData.UID, tag);
        bool FilterGroupUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u, GroupFullInfoDto group)
            => u.Value.Exists(g => string.Equals(g.GID, group.GID, StringComparison.Ordinal));
        bool FilterNotTaggedUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => u.Key.IsDirectlyPaired && !u.Key.IsOneSidedPair && !tagHandler.HasAnyTag(u.Key.UserData.UID);
        bool FilterOfflineUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => ((_cfg.Current.ShowSyncshellOfflineUsersSeparately && u.Key.IsDirectlyPaired)
                || !_cfg.Current.ShowSyncshellOfflineUsersSeparately)
                && (!u.Key.IsOneSidedPair || u.Value.Any()) && !u.Key.IsOnline && !u.Key.UserPair.OwnPermissions.IsPaused();
        bool FilterOfflineSyncshellUsers(KeyValuePair<Pair, List<GroupFullInfoDto>> u)
            => (!u.Key.IsDirectlyPaired && !u.Key.IsOnline && !u.Key.UserPair.OwnPermissions.IsPaused());

        if (_cfg.Current.ShowVisibleUsersSeparately)
        {
            var allVisiblePairs = ImmutablePairList(allPairs.Where(FilterVisibleUsers));
            var filteredVisiblePairs = BasicSortedDictionary(filteredPairs.Where(FilterVisibleUsers));
            if (allVisiblePairs.Any())
                drawFolders.Add(_drawFactory.CreateDrawTagFolder(TagHandler.CustomVisibleTag, filteredVisiblePairs, allVisiblePairs, router, tagHandler));
        }

        // Groups
        var groupFolders = new Dictionary<GroupFullInfoDto, (Dictionary<Pair, List<GroupFullInfoDto>> Pairs, ImmutableList<Pair> All)>();
        foreach (var group in pm.Groups.Values)
        {
            var pairsForGroup = filteredPairs.Where(u => FilterGroupUsers(u, group));
            if (!pairsForGroup.Any()) continue;
            groupFolders[group] = (BasicSortedDictionary(pairsForGroup), ImmutablePairList(allPairs.Where(u => FilterGroupUsers(u, group))));
        }
        if (_cfg.Current.GroupUpSyncshells)
        {
            foreach (var gf in groupFolders)
            {
                var folder = _drawFactory.CreateDrawGroupFolder(gf.Key, gf.Value.Pairs, gf.Value.All, router, tagHandler);
                drawFolders.Add(folder);
            }
        }

        // Online / All
        drawFolders.Add(_drawFactory.CreateDrawTagFolder((_cfg.Current.ShowOfflineUsersSeparately ? TagHandler.CustomOnlineTag : TagHandler.CustomAllTag),
            BasicSortedDictionary(filteredPairs.Where(FilterOnlineOrPausedSelf)), ImmutablePairList(allPairs.Where(FilterOnlineOrPausedSelf)), router, tagHandler));

        if (_cfg.Current.ShowOfflineUsersSeparately)
        {
            drawFolders.Add(_drawFactory.CreateDrawTagFolder(TagHandler.CustomOfflineTag,
                BasicSortedDictionary(filteredPairs.Where(FilterOfflineUsers)), ImmutablePairList(allPairs.Where(FilterOfflineUsers)), router, tagHandler));
            if (_cfg.Current.ShowSyncshellOfflineUsersSeparately)
            {
                drawFolders.Add(_drawFactory.CreateDrawTagFolder(TagHandler.CustomOfflineSyncshellTag,
                    BasicSortedDictionary(filteredPairs.Where(FilterOfflineSyncshellUsers)), ImmutablePairList(allPairs.Where(FilterOfflineSyncshellUsers)), router, tagHandler));
            }
        }

        // Tagged
        foreach (var tag in tagHandler.GetAllTagsSorted())
        {
            var tPairs = filteredPairs.Where(u => FilterTagusers(u, tag));
            if (!tPairs.Any()) continue;
            drawFolders.Add(_drawFactory.CreateDrawTagFolder(tag, BasicSortedDictionary(tPairs), ImmutablePairList(allPairs.Where(u => FilterTagusers(u, tag))), router, tagHandler));
        }

        // Unpaired
        var unpaired = filteredPairs.Where(u => !u.Key.IsDirectlyPaired && !u.Key.IsOneSidedPair && !u.Value.Any());
        if (unpaired.Any())
            drawFolders.Add(_drawFactory.CreateDrawTagFolder(TagHandler.CustomUnpairedTag, BasicSortedDictionary(unpaired), ImmutablePairList(allPairs.Where(u => !u.Key.IsDirectlyPaired && !u.Key.IsOneSidedPair && !u.Value.Any())), router, tagHandler));

        return drawFolders;
    }
// [moved]

    // Configured server variants (by server index)
    public void DrawConfiguredHeader(int serverIndex)
    {
        var hubState = _multi.GetConfiguredState(serverIndex);
        var buttonSize = _ui.GetIconButtonSize(FontAwesomeIcon.Link);

        int? online = null;
        string shard = string.Empty;
        
        try
        {
            var (onlineResult, shardResult) = _multi.GetConfiguredOnlineAsync(serverIndex, CancellationToken.None).GetAwaiter().GetResult();
            online = onlineResult;
            shard = shardResult ?? string.Empty;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to get configured online info for server {idx}", serverIndex);
        }
        var onlineStr = online.HasValue ? online.Value.ToString() : "?";
        var userSize = ImGui.CalcTextSize(onlineStr);
        var textSize = ImGui.CalcTextSize("Users Online");
        var shardText = string.IsNullOrEmpty(shard) ? string.Empty : $"Shard: {shard}";
        var shardSize = ImGui.CalcTextSize(shardText);
        var cdnHost = _multi.GetConfiguredCdnHost(serverIndex) ?? "-";
        var lastPush = _multi.GetConfiguredLastPushUtc(serverIndex);
        var lastPushText = lastPush.HasValue ? ($"Last push: {FormatAgo(lastPush.Value)}") : "Last push: -";
        var cdnText = $"CDN: {cdnHost}";
        var cdnSize = ImGui.CalcTextSize(cdnText);
        var pushSize = ImGui.CalcTextSize(lastPushText);

        ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth()) / 2 - (userSize.X + textSize.X) / 2 - ImGui.GetStyle().ItemSpacing.X / 2);
        if (hubState == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
        {
            if (!string.IsNullOrEmpty(shardText)) ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ImGuiColors.ParsedGreen, onlineStr);
            ImGui.SameLine();
            if (!string.IsNullOrEmpty(shardText)) ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Users Online");
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ImGuiColors.DalamudRed, "Not connected");
        }

        if (!string.IsNullOrEmpty(shardText))
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y);
            ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth()) / 2 - shardSize.X / 2);
            ImGui.TextUnformatted(shardText);
        }

        // CDN + Last push lines
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y);
        ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth()) / 2 - cdnSize.X / 2);
        ImGui.TextColored(ImGuiColors.DalamudGrey, cdnText);
        ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth()) / 2 - pushSize.X / 2);
        ImGui.TextColored(ImGuiColors.DalamudGrey, lastPushText);

        // Reconnect on Login/DC Travel toggle (per configured server)
        try
        {
            var storage = _servers.GetServerByIndex(serverIndex);
            bool autoReconnect = storage.ReconnectOnLoginOrDcTravel;
            var chkWidth = ImGui.CalcTextSize("Reconnect on login/DC Travel").X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;
            ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth()) / 2 - chkWidth / 2);
            if (ImGui.Checkbox("Reconnect on login/DC Travel##reconn" + serverIndex, ref autoReconnect))
            {
                storage.ReconnectOnLoginOrDcTravel = autoReconnect;
                _servers.Save();
            }
        }
        catch { }

        // Centered UID (click to copy) for this configured server
        try
        {
            var router = new ConfiguredApiActionRouter(_multi, serverIndex);
            var uid = router.UID;
            if (!string.IsNullOrWhiteSpace(uid))
            {
                using (_ui.UidFont.Push())
                {
                    var uidSize = ImGui.CalcTextSize(uid);
                    ImGui.SetCursorPosX((ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth()) / 2 - uidSize.X / 2);
                    ImGui.TextColored(ImGuiColors.ParsedGreen, uid);
                }
                if (ImGui.IsItemClicked()) ImGui.SetClipboardText(uid);
                UiSharedService.AttachToolTip("Click to copy");
            }
        }
        catch { }

        var connected = hubState is Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected or Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connecting or Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Reconnecting;
        var color = UiSharedService.GetBoolColor(!connected);
        var icon = connected ? FontAwesomeIcon.Unlink : FontAwesomeIcon.Link;
        ImGui.SameLine(ImGui.GetWindowContentRegionMin().X + UiSharedService.GetWindowContentRegionWidth() - buttonSize.X);
        if (!string.IsNullOrEmpty(shardText))
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ((userSize.Y + textSize.Y) / 2 + shardSize.Y) / 2 - ImGui.GetStyle().ItemSpacing.Y + buttonSize.Y / 2);
        }
        using (ImRaii.PushColor(ImGuiCol.Text, color))
        {
            if (_ui.IconButton(icon))
            {
                try
                {
                    if (connected)
                        _ = _multi.DisconnectConfiguredAsync(serverIndex);
                    else
                        _ = _multi.ConnectConfiguredAsync(serverIndex);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to toggle configured server {idx}", serverIndex);
                }
            }
        }
        UiSharedService.AttachToolTip((connected ? "Disconnect" : "Connect") + " configured server");
    }

    private static string FormatAgo(DateTime tsUtc)
    {
        var delta = DateTime.UtcNow - tsUtc;
        if (delta.TotalSeconds < 1) return "just now";
        if (delta.TotalSeconds < 60) return $"{(int)delta.TotalSeconds}s ago";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }

    public void DrawConfigured(int serverIndex)
    {
        using (ImRaii.PushId("cfgview-" + serverIndex))
        {
            DrawConfiguredHeader(serverIndex);
            ImGui.Separator();

            // Per-server quick actions: Add Pair + Join Syncshell
            var router = new ConfiguredApiActionRouter(_multi, serverIndex);
            DrawConfiguredQuickActions(serverIndex, router);
            ImGui.Separator();

            var pm = _multi.GetPairManagerForConfigured(serverIndex);
            TrySeedFromConfiguredCachesThrottled(serverIndex, pm);
            
            var tagHandler = new TagHandler(_servers, _servers.GetServerByIndex(serverIndex).ServerUri);

            string filterKey = $"cfg-{serverIndex}";
            string filter = _filters.TryGetValue((SyncService)(-1 - serverIndex), out var f) ? f : string.Empty; // store per-configured idx using a disjoint key
            using (ImRaii.PushId(filterKey))
            {
                ImGui.SetNextItemWidth(UiSharedService.GetWindowContentRegionWidth());
                ImGui.InputTextWithHint("##filter", "Filter by UID/Alias/Tags", ref filter, 64);
            }
            _filters[(SyncService)(-1 - serverIndex)] = filter;

            // Only rebuild folders if refreshing or if filter changed
            var drawFolders = BuildFolders(pm, tagHandler, router, filter);

            foreach (var folder in drawFolders)
            {
                folder.Draw();
            }
        }
    }

    private void DrawConfiguredQuickActions(int serverIndex, ConfiguredApiActionRouter router)
    {
        using (ImRaii.PushId("actions-" + serverIndex))
        {
            var hub = _multi.GetConfiguredHub(serverIndex);
            var connected = hub != null && hub.State == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected;

            // Add player (UID/Alias)
            string pairToAdd = _pairToAddByServer.TryGetValue(serverIndex, out var p) ? p : string.Empty;
            var btnSize = _ui.GetIconTextButtonSize(FontAwesomeIcon.UserPlus, "Add");
            ImGui.SetNextItemWidth(UiSharedService.GetWindowContentRegionWidth() - btnSize - ImGui.GetStyle().ItemSpacing.X - 140f);
            ImGui.InputTextWithHint("##pairToAdd", "UID or Alias", ref pairToAdd, 64);
            _pairToAddByServer[serverIndex] = pairToAdd;
            ImGui.SameLine();
            using (ImRaii.Disabled(!connected || string.IsNullOrWhiteSpace(pairToAdd)))
            {
                if (_ui.IconTextButton(FontAwesomeIcon.UserPlus, "Add"))
                {
                    _ = router.UserAddPair(new(new(pairToAdd)));
                    _pairToAddByServer[serverIndex] = string.Empty;
                }
            }
            UiSharedService.AttachToolTip("Add individual pair on this server");

            ImGui.SameLine();
            using (ImRaii.Disabled(!connected))
            {
                if (_ui.IconTextButton(FontAwesomeIcon.Users, "Join Syncshell", 140f))
                {
                    _showJoinModalByServer[serverIndex] = true;
                    ImGui.OpenPopup($"JoinSyncshell##modal-{serverIndex}");
                }
            }
            UiSharedService.AttachToolTip("Join an existing Syncshell on this server");

            // Modal content for join
            var open = _showJoinModalByServer.TryGetValue(serverIndex, out var show) && show;
            if (ImGui.BeginPopupModal($"JoinSyncshell##modal-{serverIndex}", ref open, UiSharedService.PopupWindowFlags))
            {
                _showJoinModalByServer[serverIndex] = open;
                var joinInfo = _joinInfoByServer.TryGetValue(serverIndex, out var ji) ? ji : null;
                string shellId = _joinShellIdByServer.TryGetValue(serverIndex, out var id) ? id : string.Empty;
                string pw = _joinShellPwByServer.TryGetValue(serverIndex, out var pwv) ? pwv : string.Empty;
                string prevPw = _joinShellPrevPwByServer.TryGetValue(serverIndex, out var ppw) ? ppw : string.Empty;

                using (_ui.UidFont.Push())
                    ImGui.TextUnformatted(joinInfo == null || !joinInfo.Success ? "Join Syncshell" : "Finalize join " + joinInfo.GroupAliasOrGID);
                ImGui.Separator();

                if (joinInfo == null || !joinInfo.Success)
                {
                    ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted("Syncshell ID"); ImGui.SameLine(180);
                    ImGui.InputTextWithHint("##gid", "Full Syncshell ID", ref shellId, 64);
                    _joinShellIdByServer[serverIndex] = shellId;

                    ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted("Password"); ImGui.SameLine(180);
                    ImGui.InputTextWithHint("##gpw", "Password", ref pw, 64, ImGuiInputTextFlags.Password);
                    _joinShellPwByServer[serverIndex] = pw;

                    // Show a generic failure note when the last attempt failed
                    if (_joinAttemptFailedByServer.TryGetValue(serverIndex, out var failed) && failed)
                    {
                        UiSharedService.ColorTextWrapped("Failed to join the Syncshell. Check ID/password or server capacity/invites.", ImGuiColors.DalamudYellow);
                    }

                    using (ImRaii.Disabled(!connected || string.IsNullOrWhiteSpace(shellId) || string.IsNullOrWhiteSpace(pw)))
                    {
                        if (_ui.IconTextButton(FontAwesomeIcon.ArrowRight, "Next"))
                        {
                            try
                            {
                                var dto = hub!.InvokeAsync<GroupJoinInfoDto>("GroupJoin", new GroupPasswordDto(new GroupData(shellId), pw)).GetAwaiter().GetResult();
                                _joinInfoByServer[serverIndex] = dto;
                                _joinShellPrevPwByServer[serverIndex] = pw;
                                _joinShellPwByServer[serverIndex] = string.Empty;
                                _joinAttemptFailedByServer[serverIndex] = dto == null || !dto.Success;
                            }
                            catch { _joinInfoByServer[serverIndex] = null; _joinAttemptFailedByServer[serverIndex] = true; }
                        }
                    }
                }
                else
                {
                    ImGui.TextUnformatted("About to join: " + joinInfo.GroupAliasOrGID + " by " + joinInfo.OwnerAliasOrUID);
                    ImGuiHelpers.ScaledDummy(2f);
                    if (_ui.IconTextButton(FontAwesomeIcon.Plus, "Finalize join"))
                    {
                        try
                        {
                            // Map default group prefs to join permissions
                            var defaults = router.DefaultPermissions;
                            GroupUserPreferredPermissions joinPerms = GroupUserPreferredPermissions.NoneSet;
                            if (defaults != null)
                            {
                                joinPerms.SetDisableSounds(defaults.DisableGroupSounds);
                                joinPerms.SetDisableAnimations(defaults.DisableGroupAnimations);
                                joinPerms.SetDisableVFX(defaults.DisableGroupVFX);
                            }
                            var ok = hub!.InvokeAsync<bool>("GroupJoinFinalize", new GroupJoinDto(joinInfo.Group, prevPw, joinPerms)).GetAwaiter().GetResult();
                            if (ok)
                            {
                                // reset and close
                                _joinInfoByServer[serverIndex] = null;
                                _joinShellIdByServer[serverIndex] = string.Empty;
                                _joinShellPwByServer[serverIndex] = string.Empty;
                                _joinShellPrevPwByServer[serverIndex] = string.Empty;
                                _joinAttemptFailedByServer[serverIndex] = false;
                                _showJoinModalByServer[serverIndex] = false;
                                ImGui.CloseCurrentPopup();
                            }
                        }
                        catch { }
                    }
                }

                UiSharedService.SetScaledWindowSize(420);
                ImGui.EndPopup();
            }
        }
    }
}
