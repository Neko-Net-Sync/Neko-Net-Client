/*
   File: ServerState.cs
   Role: High-level connection state machine for the main ApiController connection, used by UI and logic.
*/
namespace NekoNetClient.WebAPI.SignalR.Utils;

/// <summary>
/// Represents the lifecycle states of the primary server connection.
/// </summary>
public enum ServerState
{
    Offline,
    Connecting,
    Reconnecting,
    Disconnecting,
    Disconnected,
    Connected,
    Unauthorized,
    VersionMisMatch,
    RateLimited,
    NoSecretKey,
    MultiChara,
    OAuthMisconfigured,
    OAuthLoginTokenStale,
    NoAutoLogon
}