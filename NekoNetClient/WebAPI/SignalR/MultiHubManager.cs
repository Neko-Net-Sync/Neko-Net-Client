using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using System.Collections.Concurrent;

namespace NekoNetClient.WebAPI.SignalR;

public sealed class MultiHubManager : MediatorSubscriberBase, IAsyncDisposable
{
    private readonly ILogger<MultiHubManager> _log;
    private readonly ServerConfigurationManager _servers;
    private readonly TokenProvider _tokens;

    private readonly ConcurrentDictionary<SyncService, HubConnection> _hubs = new();
    private bool _disposed;

    public MultiHubManager(
        ILogger<MultiHubManager> logger,
        ServerConfigurationManager servers,
        TokenProvider tokens,
        MareMediator mediator)
        : base(logger, mediator) // ← matches your base requirement (ILogger, MareMediator)
    {
        _log = logger;
        _servers = servers;
        _tokens = tokens;
    }

    public HubConnection? Get(SyncService svc) =>
        _hubs.TryGetValue(svc, out var hub) ? hub : null;

    public async Task ConnectAsync(params SyncService[] services)
    {
        foreach (var svc in services)
        {
            var hub = _hubs.GetOrAdd(svc, _ => Build(svc, CancellationToken.None));
            if (hub.State == HubConnectionState.Disconnected)
            {
                await hub.StartAsync().ConfigureAwait(false);
                _log.LogInformation("Started {svc} hub: {state}", svc, hub.State);
            }
        }
    }

    public async Task DisconnectAsync(params SyncService[] services)
    {
        foreach (var svc in services)
        {
            if (_hubs.TryGetValue(svc, out var hub))
            {
                await hub.StopAsync().ConfigureAwait(false);
                _log.LogInformation("Stopped {svc} hub", svc);
            }
        }
    }

    private HubConnection Build(SyncService svc, CancellationToken ct)
    {
        // base wss://host from current server selection
        var baseUrl = _servers.CurrentApiUrl; // e.g., wss://sync.neonneko.cc
        var endpoint = svc switch
        {
            SyncService.NekoNet => string.IsNullOrWhiteSpace(_servers.CurrentServer.ApiEndpoint) ? "/mare" : NormalizePath(_servers.CurrentServer.ApiEndpoint),
            SyncService.Lightless => "/lightless",
            SyncService.TeraSync => "/tera-sync-v2",
            _ => "/mare"
        };

        var url = baseUrl.TrimEnd('/') + endpoint;

        var transport = _servers.CurrentServer.ForceWebSockets
            ? HttpTransportType.WebSockets
            : _servers.CurrentServer.HttpTransportType;

        var builder = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = () => _tokens.GetOrUpdateToken(ct);
                options.Transports = transport;
            })
            .AddMessagePackProtocol()
            .WithAutomaticReconnect();

        var hub = builder.Build();

        hub.Closed += async (ex) =>
        {
            _log.LogWarning(ex, "{svc} hub closed (auto-reconnect will handle).", svc);
            await Task.CompletedTask;
        };
        hub.Reconnecting += (ex) =>
        {
            _log.LogInformation(ex, "{svc} hub reconnecting…", svc);
            return Task.CompletedTask;
        };
        hub.Reconnected += (id) =>
        {
            _log.LogInformation("{svc} hub reconnected: ConnId={id}", svc, id);
            return Task.CompletedTask;
        };

        // If you have per-hub handlers, register them here:
        // if (svc == SyncService.Lightless) hub.On<string>("LightlessEvent", payload => { … });

        return hub;
    }

    private static string NormalizePath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "/mare";
        return p.StartsWith('/') ? p : "/" + p;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var hub in _hubs.Values)
        {
            try { await hub.DisposeAsync(); } catch { /* ignore on shutdown */ }
        }
        _hubs.Clear();
    }
}
