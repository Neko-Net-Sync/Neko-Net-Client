using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.ServerConfiguration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NekoNetClient.WebAPI.SignalR
{
    public sealed class MultiHubManager : MediatorSubscriberBase, IAsyncDisposable
    {
        private readonly ILogger<MultiHubManager> _log;
        private readonly ServerConfigurationManager _servers;
        private readonly TokenProvider _tokens;

        private readonly ConcurrentDictionary<SyncService, HubConnection> _hubs = new();
        private bool _disposed;

        // ─────────────────────────────────────────────────────────────────────────────
        // Service endpoints map.
        // - If Path is absolute (wss://, ws://, https://, http://), it is used as-is.
        // - If Subdomain is null, we keep CurrentApiUrl's host.
        // - If Subdomain is set, we replace the left-most label in the host.
        // Adjust these three lines to your infra.
        // MultiHubManager.cs
        private sealed record ServiceSpec(string? Subdomain, string Endpoint, bool UseMareToken, bool WebSocketsOnly);

        private static readonly Dictionary<SyncService, ServiceSpec> ServiceMap = new()
        {
            [SyncService.NekoNet] = new(null, "/mare", UseMareToken: true, WebSocketsOnly: false),
            [SyncService.Lightless] = new(null, "wss://sync.lightless-sync.org/lightless", UseMareToken: false, WebSocketsOnly: true),
            [SyncService.TeraSync] = new(null, "wss://tera.terasync.app/tera-sync-v2", UseMareToken: false, WebSocketsOnly: true),
        };

        // ─────────────────────────────────────────────────────────────────────────────

        public MultiHubManager(
            ILogger<MultiHubManager> logger,
            ServerConfigurationManager servers,
            TokenProvider tokens,
            MareMediator mediator)
            : base(logger, mediator)
        {
            _log = logger;
            _servers = servers;
            _tokens = tokens;
        }

        public HubConnection? Get(SyncService svc) =>
            _hubs.TryGetValue(svc, out var hub) ? hub : null;

        public HubConnectionState GetState(SyncService svc) =>
            _hubs.TryGetValue(svc, out var hub) ? hub.State : HubConnectionState.Disconnected;

        public string GetResolvedUrl(SyncService svc) => ResolveUrl(svc);

        public async Task ConnectAsync(params SyncService[] services)
        {
            foreach (var svc in services)
            {
                var hub = _hubs.GetOrAdd(svc, _ => Build(svc, CancellationToken.None));
                if (hub.State == HubConnectionState.Disconnected)
                {
                    var url = GetResolvedUrl(svc);
                    _log.LogInformation("Connecting {svc} -> {url}", svc, url);
                    try
                    {
                        await hub.StartAsync().ConfigureAwait(false);
                        _log.LogInformation("{svc} connected. State={state}", svc, hub.State);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Failed to connect {svc} to {url}", svc, url);
                        throw;
                    }
                }
            }
        }

        public async Task DisconnectAsync(params SyncService[] services)
        {
            foreach (var svc in services)
            {
                if (_hubs.TryGetValue(svc, out var hub))
                {
                    _log.LogInformation("Disconnecting {svc}", svc);
                    try
                    {
                        await hub.StopAsync().ConfigureAwait(false);
                        _log.LogInformation("{svc} disconnected.", svc);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Failed to disconnect {svc}", svc);
                        throw;
                    }
                }
            }
        }

        private HubConnection Build(SyncService svc, CancellationToken ct)
        {
            var url = ResolveUrl(svc);

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

            // Register per-hub handlers here if needed
            // if (svc == SyncService.Lightless) hub.On<string>("LightlessEvent", payload => { … });

            return hub;
        }

        private string ResolveUrl(SyncService svc)
        {
            var (sub, raw) = ServiceMap[svc];
            raw = raw?.Trim() ?? string.Empty;

            // Absolute override (used as-is)
            if (raw.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return raw;
            }

            // Relative: build from current API URL
            var path = NormalizePath(raw);
            var baseUrl = _servers.CurrentApiUrl; // e.g., wss://connect.neko-net.cc

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
                return baseUrl.TrimEnd('/') + path; // fallback

            // Optional subdomain swap
            var host = baseUri.Host; // connect.neko-net.cc
            if (!string.IsNullOrWhiteSpace(sub))
            {
                var parts = host.Split('.');
                if (parts.Length >= 3)
                {
                    parts[0] = sub; // connect -> lightless/tera/etc
                    host = string.Join('.', parts);
                }
                else
                {
                    host = $"{sub}.{host}";
                }
            }

            var builder = new UriBuilder(baseUri)
            {
                Host = host,
                Path = path
            };

            return builder.Uri.ToString();
        }

        private static string NormalizePath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "/mare";
            return p[0] == '/' ? p : "/" + p;
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
}
