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
        private readonly ConcurrentDictionary<SyncService, string> _lastError = new();
        private bool _disposed;

        // ─────────────────────────────────────────────────────────────────────────────
        // Service endpoints map.
        // - If Endpoint is absolute (wss://, ws://, https://, http://), it is used as-is.
        // - If Subdomain is null, we keep CurrentApiUrl's host.
        // - If Subdomain is set, we replace the left-most label in the host.
        // - UseMareToken controls whether the Mare bearer token is sent.
        // - WebSocketsOnly forces WS and SkipNegotiation.
        private sealed record ServiceSpec(
            string? Subdomain,
            string Endpoint,
            bool UseMareToken,
            bool WebSocketsOnly
        );
        // Optional per-service token provider (returns the token string to send)
        private readonly ConcurrentDictionary<SyncService, Func<CancellationToken, Task<string?>>>
            _customTokenProviders = new();

        // You can set this from anywhere that resolves MultiHubManager (e.g., your settings UI code)
        public void SetCustomTokenProvider(SyncService svc, Func<CancellationToken, Task<string?>> provider)
            => _customTokenProviders[svc] = provider;

        // Adjust to your infra as needed.
        private static readonly Dictionary<SyncService, ServiceSpec> ServiceMap =
            new Dictionary<SyncService, ServiceSpec>
            {
                { SyncService.NekoNet,   new ServiceSpec(null, "/mare",                                   UseMareToken: true,  WebSocketsOnly: true) },
                { SyncService.Lightless, new ServiceSpec(null, "wss://sync.lightless-sync.org", UseMareToken: true,  WebSocketsOnly: true)  },
                { SyncService.TeraSync,  new ServiceSpec(null, "wss://tera.terasync.app/tera-sync-v2", UseMareToken: true, WebSocketsOnly: true) },            };
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

        public string? GetLastError(SyncService svc) =>
            _lastError.TryGetValue(svc, out var e) ? e : null;

        public string GetResolvedUrl(SyncService svc) => ResolveUrlAndSpec(svc).url;

        public async Task ConnectAsync(params SyncService[] services)
        {
            foreach (var svc in services)
            {
                var hub = _hubs.GetOrAdd(svc, _ => Build(svc, CancellationToken.None));
                if (hub.State == HubConnectionState.Disconnected)
                {
                    var (url, spec) = ResolveUrlAndSpec(svc);

                    // Debug token information
                    if (spec.UseMareToken || _customTokenProviders.ContainsKey(svc))
                    {
                        try
                        {
                            string? token = null;
                            if (_customTokenProviders.TryGetValue(svc, out var customProvider))
                            {
                                token = await customProvider(CancellationToken.None);
                                _log.LogDebug("Using custom token for {svc}: {tokenPrefix}...", svc, token?.Substring(0, Math.Min(10, token?.Length ?? 0)) ?? "null");
                            }
                            else if (spec.UseMareToken)
                            {
                                token = await _tokens.GetOrUpdateToken(CancellationToken.None);
                                _log.LogDebug("Using Mare token for {svc}: {tokenPrefix}...", svc, token?.Substring(0, Math.Min(10, token?.Length ?? 0)) ?? "null");
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Failed to get token for {svc}", svc);
                        }
                    }

                    _log.LogInformation("Connecting {svc} -> {url} (UseMareToken={auth}, WSOnly={ws})",
                        svc, url, spec.UseMareToken, spec.WebSocketsOnly);
                    try
                    {
                        await hub.StartAsync().ConfigureAwait(false);
                        _lastError.TryRemove(svc, out _);
                        _log.LogInformation("{svc} connected. State={state}", svc, hub.State);
                    }
                    catch (Exception ex)
                    {
                        _lastError[svc] = ex.Message;
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
            var (url, spec) = ResolveUrlAndSpec(svc);

            var transports = spec.WebSocketsOnly
                ? HttpTransportType.WebSockets
                : _servers.CurrentServer.HttpTransportType;

            var builder = new HubConnectionBuilder()
                .WithUrl(url, options =>
                {
                    options.Transports = transports;
                    options.SkipNegotiation = spec.WebSocketsOnly;

                    // Check for custom token provider first
                    if (_customTokenProviders.TryGetValue(svc, out var customProvider))
                    {
                        options.AccessTokenProvider = () => customProvider(ct);
                        _log.LogDebug("Using custom token provider for {svc}", svc);
                    }
                    // Fall back to Mare token if configured
                    else if (spec.UseMareToken)
                    {
                        options.AccessTokenProvider = () => _tokens.GetOrUpdateToken(ct);
                        _log.LogDebug("Using Mare token provider for {svc}", svc);
                    }
                    // No authentication
                    else
                    {
                        options.AccessTokenProvider = null;
                        _log.LogDebug("No authentication configured for {svc}", svc);
                    }
                })
                .AddMessagePackProtocol()
                .WithAutomaticReconnect();

            var hub = builder.Build();

            hub.Closed += ex =>
            {
                if (ex != null) _lastError[svc] = ex.Message;
                _log.LogWarning(ex, "{svc} hub closed (auto-reconnect will handle).", svc);
                return Task.CompletedTask;
            };
            hub.Reconnecting += ex =>
            {
                if (ex != null) _lastError[svc] = ex.Message;
                _log.LogInformation(ex, "{svc} hub reconnecting…", svc);
                return Task.CompletedTask;
            };
            hub.Reconnected += id =>
            {
                _lastError.TryRemove(svc, out _);
                _log.LogInformation("{svc} hub reconnected: ConnId={id}", svc, id);
                return Task.CompletedTask;
            };

            return hub;
        }

        private (string url, ServiceSpec spec) ResolveUrlAndSpec(SyncService svc)
        {
            var spec = ServiceMap[svc];
            var raw = (spec.Endpoint ?? string.Empty).Trim();

            // Absolute override (use as-is)
            if (raw.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return (raw, spec);
            }

            // Relative: build from CurrentApiUrl (and optional subdomain)
            var path = NormalizePath(raw);
            var baseUrl = _servers.CurrentApiUrl; // e.g., wss://connect.neko-net.cc

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
                return (baseUrl.TrimEnd('/') + path, spec);

            // Optional subdomain swap
            var host = baseUri.Host; // connect.neko-net.cc
            if (!string.IsNullOrWhiteSpace(spec.Subdomain))
            {
                var parts = host.Split('.');
                host = parts.Length >= 3
                    ? string.Join('.', spec.Subdomain, string.Join('.', parts.AsSpan(1)))
                    : $"{spec.Subdomain}.{host}";
            }

            var builder = new UriBuilder(baseUri)
            {
                Host = host,
                Path = path
            };

            return (builder.Uri.ToString(), spec);
        }

        private static string NormalizePath(string p)
            => string.IsNullOrWhiteSpace(p) ? "/mare" : (p[0] == '/' ? p : "/" + p);

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