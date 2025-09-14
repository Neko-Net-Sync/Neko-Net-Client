using Microsoft.Extensions.Logging;
using NekoNetClient.Services.Mediator;
using NekoNetClient.WebAPI.SignalR;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace NekoNetClient.Services.Sync;

/// <summary>
/// Lightweight session orchestrator per service. This scaffolds a per-service lifecycle
/// and reuses MultiHubManager connections and caches to provide per-tab data today.
/// It is intentionally minimal and non-invasive so we can evolve it into
/// full per-service ApiController sessions in the next pass without breaking existing flow.
/// </summary>
public sealed class MultiSessionManager : DisposableMediatorSubscriberBase
{
    private sealed class Session
    {
        public SyncService Service { get; }
        public Session(SyncService service) { Service = service; }
        public volatile bool Running;
        public CancellationTokenSource? Cts;
    }

    private readonly MultiHubManager _multiHub;
    private readonly ConcurrentDictionary<SyncService, Session> _sessions = new();

    public MultiSessionManager(ILogger<MultiSessionManager> logger, MareMediator mediator, MultiHubManager multiHub)
        : base(logger, mediator)
    {
        _multiHub = multiHub;
    }

    public bool IsRunning(SyncService svc) => _sessions.TryGetValue(svc, out var s) && s.Running;

    public async Task StartAsync(SyncService svc)
    {
        var session = _sessions.GetOrAdd(svc, s => new Session(s));
        if (session.Running) return;

        session.Cts?.Cancel();
        session.Cts = new CancellationTokenSource();
        session.Running = true;

        // Ensure hub is connected and bootstrap per-service caches
        try
        {
            await _multiHub.ConnectAsync(svc).ConfigureAwait(false);
            // Prime groups (fills group infos/gid sets) – background is fine
            _ = _multiHub.PrimeGroupListAsync(svc, session.Cts.Token);
        }
        catch
        {
            // leave running; the tab can retry and auto-connect later
        }
    }

    public Task StopAsync(SyncService svc)
    {
        if (_sessions.TryGetValue(svc, out var s))
        {
            s.Running = false;
            s.Cts?.Cancel();
        }
        return Task.CompletedTask;
    }
}

