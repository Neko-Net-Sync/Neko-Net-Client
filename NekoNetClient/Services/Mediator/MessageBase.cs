/*
   Neko-Net Client — Services.Mediator.MessageBase
   ----------------------------------------------
   Purpose
   - Base types for mediator messages. Allows messages to request same-thread execution to safely interact
     with systems requiring the framework thread.
*/
namespace NekoNetClient.Services.Mediator;

#pragma warning disable MA0048
/// <summary>
/// Base record for mediator messages. By default, messages can be processed on a background queue.
/// </summary>
public abstract record MessageBase
{
    /// <summary>
    /// When true, the mediator will execute this message handler on the current thread immediately instead
    /// of queuing it. Useful for systems that require the framework thread.
    /// </summary>
    public virtual bool KeepThreadContext => false;
}

/// <summary>
/// Marker record for messages that must be executed on the same thread as they are published.
/// </summary>
public record SameThreadMessage : MessageBase
{
    public override bool KeepThreadContext => true;
}
#pragma warning restore MA0048