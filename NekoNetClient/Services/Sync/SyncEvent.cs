using System;

namespace NekoNetClient.Services.Sync;

public enum SyncEventKind
{
    Connected,
    Disconnected,
    Reconnecting,
    Reconnected,
    UiRefresh,
    Closed
}

public readonly record struct SyncEvent(SyncEventKind Kind, string? Detail = null);

public readonly record struct UserPairSummary(string Uid, string PlayerName, bool IsOnline, bool IsPaused);

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
