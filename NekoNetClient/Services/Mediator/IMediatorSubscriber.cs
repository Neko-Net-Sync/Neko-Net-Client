//
// NekoNetClient — Services.Mediator.IMediatorSubscriber
// ------------------------------------------------------------
// Purpose:
//   Minimal contract for types that subscribe to mediator messages.
//
namespace NekoNetClient.Services.Mediator;

/// <summary>
/// Contract for mediator subscribers. Provides access to the mediator instance.
/// </summary>
public interface IMediatorSubscriber
{
    /// <summary>Gets the mediator for publishing/subscribing to messages.</summary>
    MareMediator Mediator { get; }
}