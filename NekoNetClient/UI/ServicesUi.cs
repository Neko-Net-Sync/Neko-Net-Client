using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using NekoNetClient.MareConfiguration;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.UI.Components;
using NekoNetClient.WebAPI.SignalR;
using System.Numerics;

namespace NekoNetClient.UI;

public sealed class ServicesUi : WindowMediatorSubscriberBase
{
    private readonly MultiHubManager _multi;
    private readonly ServerConfigurationManager _servers;
    private readonly DrawEntityFactory _drawFactory;
    private readonly MareConfigService _cfg;
    private readonly ServiceSessionView _sessionView;

    public ServicesUi(
        ILogger<ServicesUi> logger,
        MareMediator mediator,
        UiSharedService ui,
        MultiHubManager multi,
        ServerConfigurationManager servers,
        DrawEntityFactory drawFactory,
        MareConfigService cfg,
        PerformanceCollectorService perf)
    : base(logger, mediator, "###CrossSyncUI", perf)
    {
        _multi = multi;
        _servers = servers;
        _drawFactory = drawFactory;
        _cfg = cfg;
        _sessionView = new ServiceSessionView(
            log: logger,
            ui: ui,
            cfg: _cfg,
            multi: _multi,
            servers: _servers,
            drawFactory: _drawFactory
        );

    WindowName = "Cross Sync###CrossSyncUI";
        AllowPinning = false;
        AllowClickthrough = false;

        SizeConstraints = new WindowSizeConstraints()
        {
            MinimumSize = new Vector2(375, 400),
            MaximumSize = new Vector2(375, 2000),
        };
    }

    protected override void DrawInternal()
    {
        // Build per-server tabs from all saved servers (no Quick Connect gating)
        var servers = _servers.GetAllServers();
        if (ImGui.BeginTabBar("##svc-tabs", ImGuiTabBarFlags.NoTooltip))
        {
            for (int i = 0; i < servers.Count; i++)
            {
                var s = servers[i];
                if (string.IsNullOrEmpty(s.ServerUri)) continue;

                string host;
                try { host = new Uri(s.ServerUri).Host.ToLowerInvariant(); }
                catch { host = s.ServerUri; }

                var title = string.IsNullOrEmpty(s.ServerName) ? host : s.ServerName;
                if (ImGui.BeginTabItem(title))
                { _sessionView.DrawConfigured(i);
                    ImGui.EndTabItem();
                }
            }
            ImGui.EndTabBar();
        }
    }
}
