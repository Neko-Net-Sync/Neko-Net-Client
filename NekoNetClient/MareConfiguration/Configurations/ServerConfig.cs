using NekoNetClient.MareConfiguration.Models;
using NekoNetClient.WebAPI.SignalR;

namespace NekoNetClient.MareConfiguration.Configurations;

[Serializable]
public class ServerConfig : IMareConfiguration
{
    public int CurrentServer { get; set; } = 0;

    public List<ServerStorage> ServerStorage { get; set; } = new()
    {
        { new ServerStorage() { ServerName = ApiController.MainServer, ServerUri = ApiController.MainServiceUri, UseOAuth2 = true } },
    };

    public bool SendCensusData { get; set; } = false;
    public bool ShownCensusPopup { get; set; } = false;

    public int Version { get; set; } = 2;

    // Per-host server profiles for additional hubs
    public Dictionary<string, ServerProfileConfig> ServerProfiles { get; set; } = new();
}
