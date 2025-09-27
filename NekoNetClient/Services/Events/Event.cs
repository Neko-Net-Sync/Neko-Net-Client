//
// NekoNetClient — Services.Events.Event
// ------------------------------------------------------------
// Purpose:
//   Immutable event DTO used for user-facing logs and diagnostics. Supports
//   multiple construction patterns and a consistent ToString format.
//
using NekoNet.API.Data;

namespace NekoNetClient.Services.Events;

/// <summary>
/// Immutable user-facing event with timestamp, UID, character, source, severity, message, and server.
/// </summary>
public record Event
{
    /// <summary>Event creation time.</summary>
    public DateTime EventTime { get; }
    /// <summary>UID or alias of the actor.</summary>
    public string UID { get; }
    /// <summary>Character name when known.</summary>
    public string Character { get; }
    /// <summary>Event source component.</summary>
    public string EventSource { get; }
    /// <summary>Severity level of the event.</summary>
    public EventSeverity EventSeverity { get; }
    /// <summary>Human-readable message.</summary>
    public string Message { get; }
    /// <summary>Optional server/shard label.</summary>
    public string Server { get; init; } = string.Empty;

    /// <summary>
    /// Constructs an event with optional character name and a specific user.
    /// </summary>
    public Event(string? Character, UserData UserData, string EventSource, EventSeverity EventSeverity, string Message)
    {
        EventTime = DateTime.Now;
        this.UID = UserData.AliasOrUID;
        this.Character = Character ?? string.Empty;
        this.EventSource = EventSource;
        this.EventSeverity = EventSeverity;
        this.Message = Message;
    }

    /// <summary>Constructs an event for a specific user without a character name.</summary>
    public Event(UserData UserData, string EventSource, EventSeverity EventSeverity, string Message) : this(null, UserData, EventSource, EventSeverity, Message)
    {
    }

    /// <summary>Constructs a system-scoped event (no user context).</summary>
    public Event(string EventSource, EventSeverity EventSeverity, string Message)
        : this(new UserData(string.Empty), EventSource, EventSeverity, Message)
    {
    }

    /// <summary>Formatted single-line representation for log files.</summary>
    public override string ToString()
    {
        if (string.IsNullOrEmpty(UID))
            return $"{EventTime:HH:mm:ss.fff}\t[{EventSource}]{{{(int)EventSeverity}}}\t{Message}";
        else
        {
            if (string.IsNullOrEmpty(Character))
                return $"{EventTime:HH:mm:ss.fff}\t[{EventSource}]{{{(int)EventSeverity}}}\t<{UID}> {Message}";
            else
                return $"{EventTime:HH:mm:ss.fff}\t[{EventSource}]{{{(int)EventSeverity}}}\t<{UID}\\{Character}> {Message}";
        }
    }
}
