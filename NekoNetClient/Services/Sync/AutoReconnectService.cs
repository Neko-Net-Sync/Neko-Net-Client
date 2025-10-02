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
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private DateTime _nextAllowedRunUtc = DateTime.MinValue;
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(4);

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
        // Simple debounce to avoid running multiple times during rapid zone/login events
        var now = DateTime.UtcNow;
        if (now < _nextAllowedRunUtc) return;
        _nextAllowedRunUtc = now + DebounceWindow;

        await _runGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Small delay to ensure tokens/world state are ready after login/zone switch
            await Task.Delay(500).ConfigureAwait(false);

            Logger.LogDebug("[AutoReconnect] Triggered — checking flagged servers");

            // Iterate all configured servers and reconnect those flagged
            var count = _servers.GetServerCount();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var s = _servers.GetServerByIndex(i);
                    if (!s.ReconnectOnLoginOrDcTravel) continue;

                    Logger.LogDebug("[AutoReconnect] Connecting configured index {idx} ({name})", i, s.ServerName);
                    await _multi.ConnectConfiguredAsync(i).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "[AutoReconnect] ConnectConfiguredAsync failed for index {idx}", i);
                }
            }

            // Also attempt to bring main services up if flags are set on the current server
            try
            {
                var idx = _servers.CurrentServerIndex;
                var cur = _servers.GetServerByIndex(idx);
                if (cur.ReconnectOnLoginOrDcTravel)
                {
                    foreach (var svc in System.Enum.GetValues<SyncService>())
                    {
                        try { Logger.LogDebug("[AutoReconnect] Ensuring service {svc} is connected", svc); await _multi.ConnectAsync(svc).ConfigureAwait(false); } catch { }
                    }
                }
            }
            catch { }

            // Perform one follow-up retry for configured servers that are still disconnected
            try
            {
                await Task.Delay(RetryDelay).ConfigureAwait(false);
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var s = _servers.GetServerByIndex(i);
                        if (!s.ReconnectOnLoginOrDcTravel) continue;
                        var state = _multi.GetConfiguredState(i);
                        if (state != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected
                            && state != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connecting
                            && state != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Reconnecting)
                        {
                            Logger.LogDebug("[AutoReconnect] Retry connecting configured index {idx} ({name}), state={state}", i, s.ServerName, state);
                            await _multi.ConnectConfiguredAsync(i).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "[AutoReconnect] Retry ConnectConfiguredAsync failed for index {idx}", i);
                    }
                }
            }
            catch { }
        }
        finally
        {
            _runGate.Release();
        }
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
