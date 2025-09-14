using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

/*
 * Summary
 * -------
 * Hosted background service that subscribes to the ISyncFacade event stream
 * and logs emitted events at Trace level. Useful for diagnostics and for
 * observing facade activity without affecting behavior.
 */

namespace NekoNetClient.Services.Sync;

public sealed class SyncFacadeEventLogger : IHostedService, IDisposable
{
    private readonly ILogger<SyncFacadeEventLogger> _logger;
    private readonly ISyncFacade _facade;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public SyncFacadeEventLogger(ILogger<SyncFacadeEventLogger> logger, ISyncFacade facade)
    {
        _logger = logger;
        _facade = facade;
    }

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
