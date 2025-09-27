//
// NekoNetClient — Services.Events.EventSeverity
// ------------------------------------------------------------
// Purpose:
//   Defines severity levels for user-facing events and diagnostics.
//
namespace NekoNetClient.Services.Events;

/// <summary>Severity levels for events.</summary>
public enum EventSeverity
{
    /// <summary>Informational, non-actionable messages.</summary>
    Informational = 0,
    /// <summary>Warnings indicating potential issues.</summary>
    Warning = 1,
    /// <summary>Errors indicating failures or serious problems.</summary>
    Error = 2
}
