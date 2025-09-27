//
// NekoNetClient — Services.Mediator.IHighPriorityMediatorSubscriber
// ------------------------------------------------------------
// Purpose:
//   Marker interface for subscribers that should receive messages first.
//
namespace NekoNetClient.Services.Mediator;

/// <summary>
/// Marker interface; subscribers implementing this are prioritized during dispatch.
/// </summary>
public interface IHighPriorityMediatorSubscriber : IMediatorSubscriber { }