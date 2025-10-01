using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NekoNetClient.MareConfiguration;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.WebAPI.SignalR;
using System.Threading;
using System.Threading.Tasks;

namespace NekoNetClient.Services.Sync;

/// <summary>
/// Listens for login and DC-travel-ish events (zone switches) and reconnects any servers
/// that have the ReconnectOnLoginOrDcTravel flag enabled. Works for both main services and
/// configured cross sync servers via MultiHubManager.
/// </summary>
public sealed class AutoReconnectService : DisposableMediatorSubscriberBase, IHostedService
{
    private readonly MultiHubManager _multi;
    private readonly ServerConfigurationManager _servers;
    private readonly Services.DalamudUtilService _dalamud;

    public AutoReconnectService(ILogger<AutoReconnectService> logger, MareMediator mediator,
        MultiHubManager multi, ServerConfigurationManager servers, Services.DalamudUtilService dalamud)
        : base(logger, mediator)
    {
        _multi = multi;
        _servers = servers;
        _dalamud = dalamud;

    Mediator.Subscribe<DalamudLoginMessage>(this, msg => { _ = ReconnectFlaggedAsync(); });
    Mediator.Subscribe<ZoneSwitchEndMessage>(this, msg => { _ = ReconnectFlaggedAsync(); });
    }

    private async Task ReconnectFlaggedAsync()
    {
        // Iterate all configured servers and reconnect those flagged
        var count = _servers.GetServerCount();
        for (int i = 0; i < count; i++)
        {
            try
            {
                var s = _servers.GetServerByIndex(i);
                if (!s.ReconnectOnLoginOrDcTravel) continue;

                // Attempt connect for configured server index
                await _multi.ConnectConfiguredAsync(i).ConfigureAwait(false);
            }
            catch { /* ignore individual server failures */ }
        }

        // Also attempt to bring main services up if flags are set on the current server
        try
        {
            var idx = _servers.CurrentServerIndex;
            var cur = _servers.GetServerByIndex(idx);
            if (cur.ReconnectOnLoginOrDcTravel)
            {
                // Bring up known main services (those that are enabled/configured in MultiHubManager)
                foreach (var svc in System.Enum.GetValues<SyncService>())
                {
                    try { await _multi.ConnectAsync(svc).ConfigureAwait(false); } catch { }
                }
            }
        }
        catch { }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // If already logged in when service starts, attempt reconnection
        if (_dalamud.IsLoggedIn)
        {
            _ = ReconnectFlaggedAsync();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
