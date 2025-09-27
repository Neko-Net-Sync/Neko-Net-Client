/*
   File: MareAuthFailureException.cs
   Role: Exception thrown when authentication with the service fails and a reason string is provided by the server.
*/
namespace NekoNetClient.WebAPI.SignalR;

/// <summary>
/// Indicates that the service rejected authentication and provided a reason string.
/// </summary>
public class MareAuthFailureException : Exception
{
    /// <summary>
    /// Creates a new instance with the failure reason supplied by the server.
    /// </summary>
    public MareAuthFailureException(string reason)
    {
        Reason = reason;
    }

    /// <summary>
    /// The reason returned by the server for the authentication failure.
    /// </summary>
    public string Reason { get; }
}