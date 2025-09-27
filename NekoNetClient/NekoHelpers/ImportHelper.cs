// -------------------------------------------------------------------------------------------------
// Neko-Net Client — NekoHelpers.ImportHelper
//
// Purpose
//   Utility to import configuration from prior compatible plugins (MareSynchronos, OpenSynchronos,
//   TerasyncV2, and earlier NekoNetClient installs). It scans standard XIVLauncher plugin config
//   folders under the user's roaming AppData, selects the most recent valid configuration, and
//   migrates cache settings, server entries, OAuth token, secret keys, and character bindings.
//
// Behavior
//   - Folders are checked in preferred order and then ordered by last write time (newest first).
//   - The first directory containing both config.json and server.json is used.
//   - Cache settings are copied when the configured cache folder exists; InitialScanComplete is
//     inferred from presence of FileCache.csv to allow reuse of an existing cache.
//   - If a server with the same API base Uri already exists, it is updated and selected; otherwise a
//     new server entry is created and selected.
//   - Character authentication for the current player/world is created or updated, preferring the
//     source UID/secret-key selection when present.
//
// Notes
//   - This helper performs synchronous reads of small JSON files and does not perform any I/O that
//     would block the game for a noticeable time in typical scenarios.
//   - No network traffic is generated; the operation is purely local migration.
// -------------------------------------------------------------------------------------------------
using NekoNetClient.MareConfiguration;
using NekoNetClient.MareConfiguration.Models;
using NekoNetClient.NekoHelpers.ImportModels;
using NekoNetClient.Services;
using NekoNetClient.Services.ServerConfiguration;
using System.Text.Json;

namespace NekoNetClient.NekoHelpers;

/// <summary>
/// Provides a one-shot import routine that attempts to migrate settings from existing plugin
/// configurations found in the standard XIVLauncher plugin configuration directories.
/// </summary>
public sealed class ImportHelper
{
    private readonly MareConfigService _config;
    private readonly ServerConfigurationManager _servers;
    private readonly DalamudUtilService _dalamud;

    /// <summary>
    /// Creates a new <see cref="ImportHelper"/>.
    /// </summary>
    /// <param name="config">Configuration service to receive migrated cache settings.</param>
    /// <param name="servers">Server manager to upsert and select the imported server.</param>
    /// <param name="dalamud">Dalamud utility used to resolve the current character and world.</param>
    public ImportHelper(MareConfigService config, ServerConfigurationManager servers, DalamudUtilService dalamud)
    {
        _config = config;
        _servers = servers;
        _dalamud = dalamud;
    }

    /// <summary>
    /// Scans for compatible plugin configuration folders, deserializes config.json/server.json from
    /// the newest valid source, and migrates the cache settings and server configuration into the
    /// current client. If successful, the imported server is selected.
    /// </summary>
    /// <returns>
    /// A populated <see cref="ImportResult"/> describing the outcome. When no compatible source is
    /// found, <see cref="ImportResult.Succeeded"/> is false and <see cref="ImportResult.Error"/>
    /// contains a human-readable reason.
    /// </returns>
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

    /// <summary>
    /// Describes the outcome of a one-shot import performed by <see cref="Run"/>.
    /// </summary>
    public sealed class ImportResult
    {
        /// <summary>
        /// Indicates whether import succeeded. When false, see <see cref="Error"/>.
        /// </summary>
        public bool Succeeded { get; set; }
        /// <summary>
        /// Absolute path of the source folder used for import, when successful.
        /// </summary>
        public string? ImportedFrom { get; set; }
        /// <summary>
        /// True when an existing cache was detected and marked as ready for reuse.
        /// </summary>
        public bool CacheReused { get; set; }
        /// <summary>
        /// The display name of the imported or updated server.
        /// </summary>
        public string? ServerName { get; set; }
        /// <summary>
        /// True when an OAuth token was imported and the server is configured to use OAuth2.
        /// </summary>
        public bool UsedOAuth { get; set; }
        /// <summary>
        /// Human-readable error when <see cref="Succeeded"/> is false.
        /// </summary>
        public string? Error { get; set; }
    }
}
