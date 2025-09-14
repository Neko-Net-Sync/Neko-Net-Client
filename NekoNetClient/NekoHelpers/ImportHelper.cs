using NekoNetClient.MareConfiguration;
using NekoNetClient.MareConfiguration.Models;
using NekoNetClient.NekoHelpers.ImportModels;
using NekoNetClient.Services;
using NekoNetClient.Services.ServerConfiguration;
using System.Text.Json;

namespace NekoNetClient.NekoHelpers;

public sealed class ImportHelper
{
    private readonly MareConfigService _config;
    private readonly ServerConfigurationManager _servers;
    private readonly DalamudUtilService _dalamud;

    public ImportHelper(MareConfigService config, ServerConfigurationManager servers, DalamudUtilService dalamud)
    {
        _config = config;
        _servers = servers;
        _dalamud = dalamud;
    }

    public ImportResult Run()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var candidates = new[]
        {
            Path.Combine(appData, "XIVLauncher", "pluginConfigs", "OpenSynchronos"),
            Path.Combine(appData, "XIVLauncher", "pluginConfigs", "NekoNetClient"),
            Path.Combine(appData, "XIVLauncher", "pluginConfigs", "MareSynchronos"),
            Path.Combine(appData, "XIVLauncher", "pluginConfigs", "TerasyncV2"),
        }.Where(Directory.Exists)
         .Select(p => new DirectoryInfo(p))
         .OrderByDescending(d => d.LastWriteTimeUtc)
         .ToList();

        foreach (var dir in candidates)
        {
            var cfgPath = Path.Combine(dir.FullName, "config.json");
            var srvPath = Path.Combine(dir.FullName, "server.json");
            var fileCache = Path.Combine(dir.FullName, "FileCache.csv");

            if (!File.Exists(cfgPath) || !File.Exists(srvPath))
                continue;

            // deserialize
            var cfg = JsonSerializer.Deserialize<MareConfigModel>(File.ReadAllText(cfgPath));
            var srvRoot = JsonSerializer.Deserialize<MareServerModelRoot>(File.ReadAllText(srvPath));
            if (cfg is null || srvRoot is null || srvRoot.ServerStorage.Count == 0)
                continue;

            // ---------- CONFIG ----------
            if (!string.IsNullOrWhiteSpace(cfg.CacheFolder) && Directory.Exists(cfg.CacheFolder))
            {
                _config.Current.CacheFolder = cfg.CacheFolder;
                _config.Current.UseCompactor = cfg.UseCompactor;
                _config.Current.MaxLocalCacheInGiB = cfg.MaxLocalCacheInGiB;
                _config.Current.InitialScanComplete = File.Exists(fileCache);
                _config.Save();
            }

            // ---------- SERVER ----------
            var idx = Math.Clamp(srvRoot.CurrentServer, 0, srvRoot.ServerStorage.Count - 1);
            var src = srvRoot.ServerStorage[idx];

            // find existing by URI
            var existingIdx = Array.FindIndex(_servers.GetServerApiUrls(),
                u => string.Equals(u, src.ServerUri, StringComparison.OrdinalIgnoreCase));

            ServerStorage target;
            if (existingIdx >= 0)
            {
                target = _servers.GetServerByIndex(existingIdx);
                _servers.SelectServer(existingIdx);
            }
            else
            {
                target = new ServerStorage
                {
                    ServerName = string.IsNullOrWhiteSpace(src.ServerName) ? "Imported Server" : $"Imported: {src.ServerName}",
                    ServerUri = src.ServerUri,
                    UseOAuth2 = src.UseOAuth2,
                };
                _servers.AddServer(target);
                _servers.SelectServer(_servers.CurrentServerIndex); // keep index sane
            }

            // apply flags/tokens
            target.ServerName = string.IsNullOrWhiteSpace(src.ServerName) ? target.ServerName : src.ServerName;
            target.ServerUri = src.ServerUri;
            target.UseOAuth2 = src.UseOAuth2;

            if (src.UseOAuth2 && !string.IsNullOrWhiteSpace(src.OAuthToken))
                target.OAuthToken = src.OAuthToken;

            // secret keys
            if (src.SecretKeys.Count > 0)
            {
                foreach (var kv in src.SecretKeys.OrderBy(k => k.Key))
                {
                    target.SecretKeys[kv.Key] = new SecretKey
                    {
                        FriendlyName = kv.Value.FriendlyName,
                        Key = kv.Value.Key
                    };
                }
            }

            // character binding
            var playerName = _dalamud.GetPlayerNameAsync().GetAwaiter().GetResult();
            var worldId = _dalamud.GetHomeWorldIdAsync().GetAwaiter().GetResult();

            var tgtAuth = target.Authentications
                .FirstOrDefault(a => string.Equals(a.CharacterName, playerName, StringComparison.Ordinal) && a.WorldId == worldId);
            if (tgtAuth is null)
            {
                tgtAuth = new Authentication { CharacterName = playerName, WorldId = worldId };
                target.Authentications.Add(tgtAuth);
            }

            // choose UID: first try exact match from source, else first source auth
            var srcAuth = src.Authentications
                .FirstOrDefault(a => string.Equals(a.CharacterName, playerName, StringComparison.Ordinal) && a.WorldId == worldId)
                ?? src.Authentications.FirstOrDefault();

            if (srcAuth is not null)
            {
                tgtAuth.UID = srcAuth.UID;
                tgtAuth.SecretKeyIdx = srcAuth.SecretKeyIdx;
                tgtAuth.AutoLogin = srcAuth.AutoLogin;
            }

            _servers.Save();

            return new ImportResult
            {
                Succeeded = true,
                ImportedFrom = dir.FullName,
                CacheReused = _config.Current.InitialScanComplete,
                ServerName = target.ServerName,
                UsedOAuth = target.UseOAuth2 && !string.IsNullOrWhiteSpace(target.OAuthToken),
            };
        }

        return new ImportResult { Succeeded = false, Error = "No prior Mare/OpenSynchronos config found." };
    }

    public sealed class ImportResult
    {
        public bool Succeeded { get; set; }
        public string? ImportedFrom { get; set; }
        public bool CacheReused { get; set; }
        public string? ServerName { get; set; }
        public bool UsedOAuth { get; set; }
        public string? Error { get; set; }
    }
}
