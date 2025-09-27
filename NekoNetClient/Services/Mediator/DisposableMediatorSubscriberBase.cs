//
// NekoNetClient — Services.Mediator.DisposableMediatorSubscriberBase
// ------------------------------------------------------------
// Purpose:
//   Convenience base class for mediator subscribers that need deterministic
//   unsubscription. Disposes by unsubscribing from all messages.
//
using Microsoft.Extensions.Logging;

namespace NekoNetClient.Services.Mediator;

/// <summary>
/// Base class for mediator subscribers that should automatically unsubscribe on disposal.
/// </summary>
public abstract class DisposableMediatorSubscriberBase : MediatorSubscriberBase, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DisposableMediatorSubscriberBase"/> class.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="mediator">Mediator used for subscriptions.</param>
    protected DisposableMediatorSubscriberBase(ILogger logger, MareMediator mediator) : base(logger, mediator)
    {
    }

    /// <summary>Calls <see cref="Dispose(bool)"/> and suppresses finalization.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Performs the actual disposal logic and unsubscribes from all mediator messages.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        Logger.LogTrace("Disposing {type} ({this})", GetType().Name, this);
        UnsubscribeAll();
    }
}