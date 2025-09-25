using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using NekoNet.API.Data;
using NekoNet.API.Data.Enum;
using NekoNetClient.MareConfiguration;
using NekoNetClient.PlayerData.Pairs;
using NekoNetClient.Services;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.UI.Components;
using NekoNetClient.WebAPI.SignalR;
using System.Numerics;

namespace NekoNetClient.UI;

public sealed class CombinedSyncViewWindow : WindowMediatorSubscriberBase
{
    private readonly MultiHubManager _multiHub;
    private readonly ServerConfigurationManager _servers;
    private readonly UiSharedService _uiShared;
    private readonly DrawEntityFactory _drawFactory;

    private string _filterText = string.Empty;
    private readonly List<SyncedUser> _cachedUsers = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    public CombinedSyncViewWindow(ILogger<CombinedSyncViewWindow> logger, MareMediator mediator,
        MultiHubManager multiHub, ServerConfigurationManager servers,
        UiSharedService uiShared, DrawEntityFactory drawFactory, MareConfigService configService,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Online Players###CombinedSyncView", performanceCollectorService)
    {
        _multiHub = multiHub;
        _servers = servers;
        _uiShared = uiShared;
        _drawFactory = drawFactory;

        WindowName = "Online Players###CombinedSyncView";
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 300),
            MaximumSize = new Vector2(1200, 800)
        };
    }

    public record SyncedUser(
        UserData User,
        string PlayerName,
        bool IsVisible,
        bool IsOnline,
        SyncService? Service,
        int? ConfiguredServerIndex,
        string ServiceLabel,
        DateTime LastSeen)
    {
        public string DisplayName => string.IsNullOrEmpty(PlayerName) ? User.AliasOrUID : PlayerName;
        public string StatusText => IsVisible ? "Visible" : IsOnline ? "Online" : "Offline";
    }

    protected override void DrawInternal()
    {
        RefreshDataIfNeeded();

        // Header info
        ImGui.TextColored(ImGuiColors.ParsedGold, "Currently online synced players across all services");
        ImGui.Spacing();

        // Filter input
        ImGui.Text("Filter:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f);
        ImGui.InputText("##filter", ref _filterText, 256);
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            _filterText = string.Empty;
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
        {
            RefreshData();
        }

        ImGui.Separator();

        var filteredUsers = GetFilteredUsers();

        // Stats (showing online players)
        ImGui.Text($"Online Players: {filteredUsers.Count}");
        var onlineCount = filteredUsers.Count(u => u.IsOnline);
        ImGui.SameLine(200);
        ImGui.TextColored(ImGuiColors.ParsedGreen, $"Online: {onlineCount}");

        ImGui.Separator();

        // Table
        DrawUsersTable(filteredUsers);
    }

    private void RefreshDataIfNeeded()
    {
        if (DateTime.UtcNow - _lastRefresh > RefreshInterval)
        {
            RefreshData();
        }
    }

    private void RefreshData()
    {
        _cachedUsers.Clear();
        var seenUsers = new HashSet<string>(); // Track by UID to avoid duplicates

        // Collect from enum services (NekoNet, Lightless, etc.)
        foreach (var service in Enum.GetValues<SyncService>())
        {
            if (_multiHub.GetState(service) != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
                continue;

            var pm = _multiHub.GetPairManagerForService(service);
            var serviceName = GetServiceDisplayName(service);

            foreach (var pair in pm.DirectPairs)
            {
                _logger.LogDebug("Service pair {uid}: IsVisible={visible}, IsOnline={online}", 
                    pair.UserData.UID, pair.IsVisible, pair.IsOnline);
                
                // Only include online players
                if (!pair.IsOnline)
                    continue;

                if (!seenUsers.Add(pair.UserData.UID))
                    continue; // Skip duplicates (same user on multiple services)

                _cachedUsers.Add(new SyncedUser(
                    pair.UserData,
                    pair.PlayerName ?? string.Empty,
                    pair.IsVisible,
                    pair.IsOnline,
                    service,
                    null,
                    serviceName,
                    DateTime.UtcNow));
            }
        }

        // Collect from configured servers
        var configuredServers = _servers.GetAllServers();
        for (int i = 0; i < configuredServers.Count; i++)
        {
            var serverConfig = configuredServers[i];
            if (_multiHub.GetConfiguredState(i) != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
                continue;

            var pm = _multiHub.GetPairManagerForConfigured(i);
            var serverName = string.IsNullOrEmpty(serverConfig.ServerName) 
                ? $"Server {i + 1}" 
                : serverConfig.ServerName;

            foreach (var pair in pm.DirectPairs)
            {
                _logger.LogDebug("Configured pair {uid}: IsVisible={visible}, IsOnline={online}", 
                    pair.UserData.UID, pair.IsVisible, pair.IsOnline);
                
                // Only include online players
                if (!pair.IsOnline)
                    continue;

                var uniqueKey = $"{pair.UserData.UID}@{i}"; // Include server index for uniqueness
                if (!seenUsers.Add(uniqueKey))
                    continue;

                _cachedUsers.Add(new SyncedUser(
                    pair.UserData,
                    pair.PlayerName ?? string.Empty,
                    pair.IsVisible,
                    pair.IsOnline,
                    null,
                    i,
                    serverName,
                    DateTime.UtcNow));
            }
        }

        _lastRefresh = DateTime.UtcNow;
    }

    private List<SyncedUser> GetFilteredUsers()
    {
        if (string.IsNullOrEmpty(_filterText))
            return _cachedUsers.OrderBy(u => u.DisplayName).ToList();

        return _cachedUsers
            .Where(u => u.DisplayName.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ||
                       u.User.UID.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ||
                       u.ServiceLabel.Contains(_filterText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.DisplayName)
            .ToList();
    }

    private void DrawUsersTable(List<SyncedUser> users)
    {
        using var table = ImRaii.Table("CombinedSyncTable", 5, 
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | 
            ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable);
        
        if (!table) return;

        // Setup columns
        ImGui.TableSetupColumn("User", ImGuiTableColumnFlags.WidthFixed, 200f);
        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("Service", ImGuiTableColumnFlags.WidthFixed, 120f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 100f);
        ImGui.TableHeadersRow();

        if (users.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No online players found");
        }

        foreach (var user in users)
        {
            ImGui.TableNextRow();

            // User column
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(user.User.AliasOrUID);
            if (!string.IsNullOrEmpty(user.User.UID) && user.User.UID != user.User.Alias)
            {
                UiSharedService.AttachToolTip($"UID: {user.User.UID}");
            }

            // Character column  
            ImGui.TableSetColumnIndex(1);
            if (!string.IsNullOrEmpty(user.PlayerName))
            {
                var color = user.IsVisible ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudWhite;
                ImGui.TextColored(color, user.PlayerName);
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "Not visible");
            }

            // Service column
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(user.ServiceLabel);

            // Status column
            ImGui.TableSetColumnIndex(3);
            var statusColor = user.IsVisible ? ImGuiColors.ParsedGreen : 
                             user.IsOnline ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey;
            ImGui.TextColored(statusColor, user.StatusText);

            // Actions column
            ImGui.TableSetColumnIndex(4);
            DrawUserActions(user);
        }
    }

    private void DrawUserActions(SyncedUser user)
    {


        // Target button (only if visible)
        if (user.IsVisible)
        {
            if (_uiShared.IconButton(FontAwesomeIcon.Crosshairs))
            {
                // Find the actual pair to send target message
                var pm = user.Service.HasValue 
                    ? _multiHub.GetPairManagerForService(user.Service.Value)
                    : _multiHub.GetPairManagerForConfigured(user.ConfiguredServerIndex!.Value);

                var pair = pm.DirectPairs.FirstOrDefault(p => p.UserData.UID == user.User.UID);
                if (pair != null)
                {
                    Mediator.Publish(new TargetPairMessage(pair));
                }
            }
            UiSharedService.AttachToolTip($"Target {user.DisplayName}");

            ImGui.SameLine(0, 2f);
        }

        // Open profile button
        if (_uiShared.IconButton(FontAwesomeIcon.User))
        {
            // Find the actual pair to open profile
            var pm = user.Service.HasValue 
                ? _multiHub.GetPairManagerForService(user.Service.Value)
                : _multiHub.GetPairManagerForConfigured(user.ConfiguredServerIndex!.Value);

            var pair = pm.DirectPairs.FirstOrDefault(p => p.UserData.UID == user.User.UID);
            if (pair != null)
            {
                // Open profile for this pair
                Mediator.Publish(new ProfileOpenStandaloneMessage(pair));
                // For now, just show a tooltip
            }
        }
        UiSharedService.AttachToolTip($"Open profile for {user.DisplayName}");
    }

    private static string GetServiceDisplayName(SyncService service) => service switch
    {
        SyncService.NekoNet => "NekoNet",
        SyncService.Lightless => "Lightless", 
        SyncService.TeraSync => "TeraSync",
        _ => service.ToString()
    };
}