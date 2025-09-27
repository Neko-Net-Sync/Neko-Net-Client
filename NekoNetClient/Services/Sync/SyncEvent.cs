//
// NekoNetClient — Services.Sync.SyncEvent
// ------------------------------------------------------------
// Purpose:
//   Event model and small DTOs used by sync-facing façades and UIs.
//   Provides strongly-typed kinds and lightweight status/summary records.
//
using System;

namespace NekoNetClient.Services.Sync;

/// <summary>
/// Kinds of synchronization events emitted by the façades.
/// </summary>
public enum SyncEventKind
{
    /// <summary>Connection established.</summary>
    Connected,
    /// <summary>Connection lost or intentionally stopped.</summary>
    Disconnected,
    /// <summary>Hub is attempting to reconnect.</summary>
    Reconnecting,
    /// <summary>Hub has reconnected after a transient failure.</summary>
    Reconnected,
    /// <summary>Signal to prompt UI recomputation or repaint.</summary>
    UiRefresh,
    /// <summary>Hub closed with an error or terminal state.</summary>
    Closed
}

/// <summary>
/// A single sync event with an optional detail (e.g., display name or reason).
/// </summary>
public readonly record struct SyncEvent(SyncEventKind Kind, string? Detail = null);

/// <summary>
/// Lightweight summary of a user pair for list displays.
/// </summary>
public readonly record struct UserPairSummary(string Uid, string PlayerName, bool IsOnline, bool IsPaused);

/// <summary>
/// High-level status snapshot for a service shard.
/// </summary>
public readonly record struct SyncStatus(
    string ShardName,
    string DisplayName,
    string Uid,
    bool IsReady,
    string State,
    int OnlineUsers,
    string ClientVersion,
    bool IsCurrentVersion
);
