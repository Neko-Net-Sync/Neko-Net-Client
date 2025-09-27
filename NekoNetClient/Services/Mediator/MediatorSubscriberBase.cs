//
// NekoNetClient — Services.Mediator.MediatorSubscriberBase
// ------------------------------------------------------------
// Purpose:
//   Common base for mediator subscribers; wires logger and mediator,
//   and offers an UnsubscribeAll helper for clean shutdowns.
//
using Microsoft.Extensions.Logging;

namespace NekoNetClient.Services.Mediator;

/// <summary>
/// Base class for mediator subscribers, providing logging and unsubscribe convenience.
/// </summary>
public abstract class MediatorSubscriberBase : IMediatorSubscriber
{
    /// <summary>
    /// Creates a new instance, logs creation, and stores the mediator reference.
    /// </summary>
    protected MediatorSubscriberBase(ILogger logger, MareMediator mediator)
    {
        Logger = logger;

        Logger.LogTrace("Creating {type} ({this})", GetType().Name, this);
        Mediator = mediator;
    }

    /// <summary>Gets the mediator used for message subscriptions.</summary>
    public MareMediator Mediator { get; }
    /// <summary>Gets the logger for diagnostics.</summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Unsubscribes this instance from all messages it subscribed to.
    /// </summary>
    protected void UnsubscribeAll()
    {
        Logger.LogTrace("Unsubscribing from all for {type} ({this})", GetType().Name, this);
        Mediator.UnsubscribeAll(this);
    }
}