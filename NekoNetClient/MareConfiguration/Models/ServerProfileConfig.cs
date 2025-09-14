using System;
using System.Collections.Generic;

namespace NekoNetClient.MareConfiguration.Models;

[Serializable]
public class ServerProfileConfig
{
    public string Host { get; set; } = string.Empty;
    public bool UseJsonProtocol { get; set; } = true;
    public List<string> SystemInfoOnlinePaths { get; set; } = new() { "OnlineUsers" };
    public List<string> SystemInfoShardPaths { get; set; } = new() { "ShardName", "ServerInfo.ShardName" };
    public List<string> GroupGidPaths { get; set; } = new() { "Group.GID", "group.gid" };
}

