using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
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

// Lightweight, reusable view that renders a session (pairs + folders) for a specific service.
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

        var (online, shard) = _multi.GetServiceOnlineAsync(svc, CancellationToken.None).GetAwaiter().GetResult();
        var onlineStr = online.HasValue ? online.Value.ToString() : "?";
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
            // Seed from cached hub data if the service PairManager is empty
            TrySeedFromCaches(svc, pm);
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

    private void TrySeedFromCaches(SyncService svc, PairManager pm)
    {
        try
        {
            if (_multi.GetState(svc) != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
            {
                return; // don't seed when disconnected; UI should show empty
            }
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
            => u.OrderByDescending(u => u.Key.IsVisible)
                .ThenByDescending(u => u.Key.IsOnline)
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

        var (online, shard) = _multi.GetConfiguredOnlineAsync(serverIndex, CancellationToken.None).GetAwaiter().GetResult();
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

            var pm = _multi.GetPairManagerForConfigured(serverIndex);
            var router = new ConfiguredApiActionRouter(_multi, serverIndex);
            var tagHandler = new TagHandler(_servers, _servers.GetServerByIndex(serverIndex).ServerUri);

            string filterKey = $"cfg-{serverIndex}";
            string filter = _filters.TryGetValue((SyncService)(-1 - serverIndex), out var f) ? f : string.Empty; // store per-configured idx using a disjoint key
            using (ImRaii.PushId(filterKey))
            {
                ImGui.SetNextItemWidth(UiSharedService.GetWindowContentRegionWidth());
                ImGui.InputTextWithHint("##filter", "Filter by UID/Alias/Tags", ref filter, 64);
            }
            _filters[(SyncService)(-1 - serverIndex)] = filter;

            var drawFolders = BuildFolders(pm, tagHandler, router, filter);

            foreach (var folder in drawFolders)
            {
                folder.Draw();
            }
        }
    }
}
