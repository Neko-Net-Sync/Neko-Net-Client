using Microsoft.AspNetCore.Http.Connections;

namespace NekoNetClient.MareConfiguration.Models;

[Serializable]
public class ServerStorage
{
    public List<Authentication> Authentications { get; set; } = [];
    public bool FullPause { get; set; } = false;
    public Dictionary<int, SecretKey> SecretKeys { get; set; } = [];
    public string ServerName { get; set; } = string.Empty;
    public string ServerUri { get; set; } = string.Empty;
    public bool UseOAuth2 { get; set; } = false;
    public string? OAuthToken { get; set; } = null;
    public HttpTransportType HttpTransportType { get; set; } = HttpTransportType.WebSockets;
    public bool ForceWebSockets { get; set; } = false;
    public string? ApiEndpoint { get; set; }
    public bool UseMungeUpload { get; set; } = false;
    public bool UseDirectDownload { get; set; } = false;
    /// <summary>
    /// When enabled, the client will attempt to reconnect to this server when the player logs in
    /// or performs DC Travel (detected via world change after a zone switch).
    /// </summary>
    public bool ReconnectOnLoginOrDcTravel { get; set; } = false;
}
