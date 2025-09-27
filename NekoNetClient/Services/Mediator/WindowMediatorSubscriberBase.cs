//
// NekoNetClient — Services.Mediator.WindowMediatorSubscriberBase
// ------------------------------------------------------------
// Purpose:
//   Base class for mediator-aware Dalamud windows. Wires a UiToggleMessage handler,
//   wraps Draw() with performance logging, and unsubscribes on dispose.
//
using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;

namespace NekoNetClient.Services.Mediator;

/// <summary>
/// Base class for mediator-aware windows that toggle via <see cref="UiToggleMessage"/>.
/// Provides performance-wrapped <see cref="Draw"/> and automatic unsubscription.
/// </summary>
public abstract class WindowMediatorSubscriberBase : Window, IMediatorSubscriber, IDisposable
{
    protected readonly ILogger _logger;
    private readonly PerformanceCollectorService _performanceCollectorService;

    /// <summary>
    /// Initializes a new window and subscribes to <see cref="UiToggleMessage"/> for toggling.
    /// </summary>
    protected WindowMediatorSubscriberBase(ILogger logger, MareMediator mediator, string name,
        PerformanceCollectorService performanceCollectorService) : base(name)
    {
        _logger = logger;
        Mediator = mediator;
        _performanceCollectorService = performanceCollectorService;
        _logger.LogTrace("Creating {type}", GetType());

        Mediator.Subscribe<UiToggleMessage>(this, (msg) =>
        {
            if (msg.UiType == GetType())
            {
                Toggle();
            }
        });
    }

    /// <inheritdoc />
    public MareMediator Mediator { get; }

    /// <summary>Disposes the window and unsubscribes from the mediator.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Wraps <see cref="DrawInternal"/> with performance logging.
    /// </summary>
    public override void Draw()
    {
        _performanceCollectorService.LogPerformance(this, $"Draw", DrawInternal);
    }

    /// <summary>Derived windows implement their drawing logic here.</summary>
    protected abstract void DrawInternal();

    /// <summary>No-op stop hook for hosted semantics compatibility.</summary>
    public virtual Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Unsubscribes from all mediator messages and logs disposal.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        _logger.LogTrace("Disposing {type}", GetType());

        Mediator.UnsubscribeAll(this);
    }
}