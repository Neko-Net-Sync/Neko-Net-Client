//
// NekoNetClient — Services.Sync.MultiSessionManager
// ------------------------------------------------------------
// Purpose:
//   Minimal, per-service session scaffolding on top of MultiHubManager.
//   Manages tab/service lifecycles without changing connection semantics.
//   Designed to be evolved into richer per-service sessions later on.
//
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
        /// <summary>The service this session is tied to.</summary>
        public SyncService Service { get; }
        public Session(SyncService service) { Service = service; }
        /// <summary>Indicates whether this session is running.</summary>
        public volatile bool Running;
        /// <summary>Cancellation controlling any background priming tasks.</summary>
        public CancellationTokenSource? Cts;
    }

    private readonly MultiHubManager _multiHub;
    private readonly ConcurrentDictionary<SyncService, Session> _sessions = new();

    /// <summary>
    /// Creates a new <see cref="MultiSessionManager"/>.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostics.</param>
    /// <param name="mediator">Mediator used by the base class for subscriptions.</param>
    /// <param name="multiHub">Hub manager that handles connections per service.</param>
    public MultiSessionManager(ILogger<MultiSessionManager> logger, MareMediator mediator, MultiHubManager multiHub)
        : base(logger, mediator)
    {
        _multiHub = multiHub;
    }

    /// <summary>
    /// Returns whether the specified service has an active, running session.
    /// </summary>
    public bool IsRunning(SyncService svc) => _sessions.TryGetValue(svc, out var s) && s.Running;

    /// <summary>
    /// Starts the session for the given service. Ensures a connection via <see cref="MultiHubManager"/>
    /// and primes group data in the background. Idempotent.
    /// </summary>
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

    /// <summary>
    /// Stops the session for the given service and cancels background priming.
    /// Does not forcibly disconnect the underlying hub (shared across sessions).
    /// </summary>
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

