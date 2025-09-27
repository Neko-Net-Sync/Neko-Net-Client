using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

//
// NekoNetClient — Services.Sync.SyncFacadeEventLogger
// ------------------------------------------------------------
// Purpose:
//   Hosted background service that subscribes to the ISyncFacade event stream
//   and logs emitted events at Trace level. Useful for diagnostics and for
//   observing façade activity without affecting behavior.
//

namespace NekoNetClient.Services.Sync;

public sealed class SyncFacadeEventLogger : IHostedService, IDisposable
{
    private readonly ILogger<SyncFacadeEventLogger> _logger;
    private readonly ISyncFacade _facade;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    /// <summary>
    /// Creates a new <see cref="SyncFacadeEventLogger"/> bound to a facade.
    /// </summary>
    public SyncFacadeEventLogger(ILogger<SyncFacadeEventLogger> logger, ISyncFacade facade)
    {
        _logger = logger;
        _facade = facade;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var ev in _facade.EventsAsync(_loopCts.Token).ConfigureAwait(false))
                {
                    _logger.LogTrace("SyncFacade event: {Kind} {Detail}", ev.Kind, ev.Detail);
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }, _loopCts.Token);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _loopCts?.Cancel();
        if (_loopTask != null)
        {
            try { await _loopTask.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
    }
}
