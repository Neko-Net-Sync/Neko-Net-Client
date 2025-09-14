using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NekoNetClient.Services.Sync;

public interface ISyncFacade
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);

    bool IsReady { get; }

    IAsyncEnumerable<SyncEvent> EventsAsync(CancellationToken ct);

    Task<IReadOnlyList<UserPairSummary>> GetPairsAsync(CancellationToken ct);

    Task<SyncStatus> GetStatusAsync(CancellationToken ct);
}
