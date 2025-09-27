//
// NekoNetClient — Services.Sync.ISyncFacade
// ------------------------------------------------------------
// Purpose:
//   Defines a minimal façade over a single-service sync session.
//   It exposes lifecycle control (start/stop), a readiness flag,
//   an async event stream for UI/reactive consumers, and summary
//   queries for pair lists and overall sync status.
//
// Notes:
//   - This interface is intentionally small to keep call sites simple.
//   - Implementations must avoid blocking and prefer async patterns.
//   - Events are delivered via IAsyncEnumerable to enable backpressure
//     and cancellation-aware consumption in UI loops.
//
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NekoNetClient.Services.Sync;

/// <summary>
/// Façade for interacting with a single-service synchronization session.
/// Provides lifecycle control, readiness state, an async event stream, and lightweight queries.
/// </summary>
public interface ISyncFacade
{
    /// <summary>
    /// Starts the synchronization session asynchronously.
    /// Implementations should be idempotent and safe to call multiple times.
    /// </summary>
    /// <param name="ct">Cancellation token that aborts the startup operation.</param>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Stops the synchronization session asynchronously and releases any related resources.
    /// Implementations should be idempotent.
    /// </summary>
    /// <param name="ct">Cancellation token that aborts the shutdown operation.</param>
    Task StopAsync(CancellationToken ct);

    /// <summary>
    /// Gets a value indicating whether the façade is fully connected and ready.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Subscribes to the event stream produced by the synchronization session.
    /// The stream completes when the token is cancelled or the façade is disposed.
    /// </summary>
    /// <param name="ct">Cancellation token to stop the enumeration.</param>
    /// <returns>An asynchronous stream of <see cref="SyncEvent"/> values.</returns>
    IAsyncEnumerable<SyncEvent> EventsAsync(CancellationToken ct);

    /// <summary>
    /// Returns the current list of pairs known by the session.
    /// Intended for lightweight UI population or diagnostics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An immutable snapshot of user pair summaries.</returns>
    Task<IReadOnlyList<UserPairSummary>> GetPairsAsync(CancellationToken ct);

    /// <summary>
    /// Retrieves a snapshot of the current high-level synchronization status.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current <see cref="SyncStatus"/>.</returns>
    Task<SyncStatus> GetStatusAsync(CancellationToken ct);
}
