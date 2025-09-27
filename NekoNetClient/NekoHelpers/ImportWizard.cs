// -------------------------------------------------------------------------------------------------
// Neko-Net Client — NekoHelpers.ImportWizard
//
// Purpose
//   UI-oriented helper that separates scanning of a source folder (containing config.json and
//   server.json) from the actual import operation. Designed to support a multi-select UI: the scan
//   returns a list of candidate servers and metadata; a subsequent call imports only the selected
//   entries and optionally configures cache reuse.
// -------------------------------------------------------------------------------------------------
using System.Text.Json;
using NekoNetClient.MareConfiguration;
using NekoNetClient.MareConfiguration.Models;
using NekoNetClient.NekoHelpers.ImportModels;
using NekoNetClient.Services.ServerConfiguration;

namespace NekoNetClient.NekoHelpers;

/// <summary>
/// Provides a two-phase import flow for UI: scan a folder for compatible configuration and then
/// import a user-selected subset of servers and cache settings.
/// </summary>
public sealed class ImportWizard
{
    private readonly MareConfigService _config;
    private readonly ServerConfigurationManager _servers;

    /// <summary>
    /// Creates a new <see cref="ImportWizard"/>.
    /// </summary>
    /// <param name="config">Configuration service to write cache choices.</param>
    /// <param name="servers">Server manager to upsert imported servers.</param>
    public ImportWizard(MareConfigService config, ServerConfigurationManager servers)
    {
        _config  = config;
        _servers = servers;
    }

    /// <summary>
    /// Scans a folder for config.json and server.json, deserializes their contents, and composes a
    /// <see cref="ScanResult"/> suitable for driving a selection UI.
    /// </summary>
    /// <param name="folder">Absolute path to the folder to scan.</param>
    /// <returns>Scan result including candidate servers and cache metadata, or an error.</returns>
    public ScanResult ScanFolder(string folder)
    {
        var res = new ScanResult { SourceFolder = folder };

        var cfgPath = Path.Combine(folder, "config.json");
        var srvPath = Path.Combine(folder, "server.json");
        var fileCache = Path.Combine(folder, "FileCache.csv");

        if (!File.Exists(srvPath))
        {
            res.Error = "server.json not found.";
            return res;
        }

        try
        {
            res.SourceConfig = File.Exists(cfgPath)
                ? JsonSerializer.Deserialize<MareConfigModel>(File.ReadAllText(cfgPath))
                : null;

            var root = JsonSerializer.Deserialize<MareServerModelRoot>(File.ReadAllText(srvPath));
            if (root == null || root.ServerStorage.Count == 0)
            {
                res.Error = "No servers in server.json.";
                return res;
            }

            res.SourceServers = root.ServerStorage
                .Select(s => new ServerRow
                {
                    Selected   = true,
                    ServerName = string.IsNullOrWhiteSpace(s.ServerName) ? "(unnamed)" : s.ServerName,
                    ServerUri  = s.ServerUri ?? "",
                    UseOAuth2  = s.UseOAuth2,
                    HasToken   = s.UseOAuth2 && !string.IsNullOrWhiteSpace(s.OAuthToken),
                    AuthCount  = s.Authentications?.Count ?? 0,
                    Source     = s
                })
                .ToList();

            res.SourceCurrentIndex = root.CurrentServer;
            res.FileCacheExists = File.Exists(fileCache);
            res.SuggestedCacheFolder = res.SourceConfig?.CacheFolder ?? "";
        }
        catch (Exception ex)
        {
            res.Error = $"Scan failed: {ex.Message}";
        }

        return res;
    }

    /// <summary>
    /// Imports the selected server rows from a previous <see cref="ScanFolder"/> call and applies
    /// the user's cache choices. Existing matching servers are updated; non-existent servers are
    /// created. The selected server in the source is preserved by selecting its counterpart here.
    /// </summary>
    /// <param name="scan">The prior scan result.</param>
    /// <param name="selectedRows">Rows chosen by the user for import.</param>
    /// <param name="cacheFolder">Optional cache folder to configure.</param>
    /// <param name="reuseCache">Whether to mark the cache as ready if a FileCache.csv was found.</param>
    /// <returns>A summary of creations, updates, and cache configuration.</returns>
    public ImportSummary ImportSelected(ScanResult scan, IReadOnlyList<ServerRow> selectedRows, string? cacheFolder, bool reuseCache)
    {
        var sum = new ImportSummary();
        if (selectedRows.Count == 0) { sum.Error = "No servers selected."; return sum; }

        // --- Cache choice
        if (!string.IsNullOrWhiteSpace(cacheFolder) && Directory.Exists(cacheFolder))
        {
            _config.Current.CacheFolder = cacheFolder!;
            _config.Current.UseCompactor = scan.SourceConfig?.UseCompactor ?? _config.Current.UseCompactor;
            _config.Current.MaxLocalCacheInGiB = scan.SourceConfig?.MaxLocalCacheInGiB ?? _config.Current.MaxLocalCacheInGiB;
            _config.Current.InitialScanComplete = reuseCache && scan.FileCacheExists;
            _config.Save();
            sum.CacheConfigured = true;
            sum.CacheReused = _config.Current.InitialScanComplete;
        }

        // --- Servers
        var currentTargetIndex = -1;

        for (int i = 0; i < scan.SourceServers.Count; i++)
        {
            var row = scan.SourceServers[i];
            if (!selectedRows.Contains(row)) continue;

            var src = row.Source!;
            var existingIdx = Array.FindIndex(_servers.GetServerApiUrls(),
                u => string.Equals(u, src.ServerUri, StringComparison.OrdinalIgnoreCase));

            ServerStorage target;
            if (existingIdx >= 0)
            {
                target = _servers.GetServerByIndex(existingIdx);
                sum.Updated++;
                if (i == scan.SourceCurrentIndex) currentTargetIndex = existingIdx;
            }
            else
            {
                target = new ServerStorage
                {
                    ServerName = string.IsNullOrWhiteSpace(src.ServerName) ? "Imported Server" : $"Imported: {src.ServerName}",
                    ServerUri  = src.ServerUri,
                    UseOAuth2  = src.UseOAuth2,
                };
                _servers.AddServer(target);

                // find its new index
                var newIdx = Array.FindIndex(_servers.GetServerApiUrls(),
                    u => string.Equals(u, src.ServerUri, StringComparison.OrdinalIgnoreCase));
                if (i == scan.SourceCurrentIndex) currentTargetIndex = newIdx;

                sum.Created++;
            }

            // update basic fields
            target.ServerName = string.IsNullOrWhiteSpace(src.ServerName) ? target.ServerName : src.ServerName;
            target.ServerUri  = src.ServerUri;
            target.UseOAuth2  = src.UseOAuth2;
            if (src.UseOAuth2 && !string.IsNullOrWhiteSpace(src.OAuthToken))
                target.OAuthToken = src.OAuthToken;

            // merge secret keys
            if (src.SecretKeys?.Count > 0)
            {
                int NextFree() => target.SecretKeys.Count == 0 ? 0 : target.SecretKeys.Keys.Max() + 1;

                foreach (var kv in src.SecretKeys.OrderBy(k => k.Key))
                {
                    var useIdx = target.SecretKeys.ContainsKey(kv.Key) ? NextFree() : kv.Key;
                    target.SecretKeys[useIdx] = new SecretKey
                    {
                        FriendlyName = kv.Value.FriendlyName,
                        Key          = kv.Value.Key
                    };
                }
            }

            // merge authentications (preserve existing UID when present)
            if (src.Authentications != null)
            {
                foreach (var a in src.Authentications)
                {
                    var tgt = target.Authentications.FirstOrDefault(x =>
                        string.Equals(x.CharacterName, a.CharacterName, StringComparison.Ordinal) &&
                        x.WorldId == a.WorldId);

                    if (tgt == null)
                    {
                        target.Authentications.Add(new Authentication
                        {
                            CharacterName = a.CharacterName,
                            WorldId       = a.WorldId,
                            UID           = a.UID,
                            SecretKeyIdx  = a.SecretKeyIdx,
                            AutoLogin     = a.AutoLogin
                        });
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(tgt.UID) && !string.IsNullOrEmpty(a.UID))
                            tgt.UID = a.UID;
                        if (tgt.SecretKeyIdx == -1 && a.SecretKeyIdx != -1)
                            tgt.SecretKeyIdx = a.SecretKeyIdx;
                        tgt.AutoLogin = tgt.AutoLogin || a.AutoLogin;
                    }
                }
            }
        }

        if (currentTargetIndex >= 0)
            _servers.SelectServer(currentTargetIndex);

        _servers.Save();
        return sum;
    }

    // --------- DTOs for UI ---------
    /// <summary>
    /// Result from scanning a folder, including candidate servers and cache hints.
    /// </summary>
    public sealed class ScanResult
    {
        /// <summary>Folder that was scanned.</summary>
        public string SourceFolder { get; set; } = "";
        /// <summary>Human-readable error when the scan fails.</summary>
        public string? Error { get; set; }
        /// <summary>Whether a legacy FileCache.csv exists, indicating potential cache reuse.</summary>
        public bool FileCacheExists { get; set; }
        /// <summary>Suggested cache folder read from the source config, if any.</summary>
        public string SuggestedCacheFolder { get; set; } = "";
        /// <summary>Currently selected server index in the source.</summary>
        public int SourceCurrentIndex { get; set; }
        /// <summary>Deserialized source config.json, when present.</summary>
        public MareConfigModel? SourceConfig { get; set; }
        /// <summary>List of discovered servers materialized for selection.</summary>
        public List<ServerRow> SourceServers { get; set; } = new();
    }

    /// <summary>
    /// UI row representing a selectable server during import.
    /// </summary>
    public sealed class ServerRow
    {
        /// <summary>Whether the row is selected for import.</summary>
        public bool Selected { get; set; }
        /// <summary>Display name of the server.</summary>
        public string ServerName { get; set; } = "";
        /// <summary>API base URL of the server.</summary>
        public string ServerUri { get; set; } = "";
        /// <summary>True if the source server was configured with OAuth2.</summary>
        public bool UseOAuth2 { get; set; }
        /// <summary>True if an OAuth token was present in the source.</summary>
        public bool HasToken { get; set; }
        /// <summary>Number of authentications defined for the server.</summary>
        public int AuthCount { get; set; }
        /// <summary>Reference to the source server entry for detailed fields.</summary>
        public MareServerEntry? Source { get; set; }
    }

    /// <summary>
    /// Summary of the import operation including counts and cache configuration outcome.
    /// </summary>
    public sealed class ImportSummary
    {
        /// <summary>Number of newly created server entries.</summary>
        public int Created { get; set; }
        /// <summary>Number of existing server entries that were updated.</summary>
        public int Updated { get; set; }
        /// <summary>Indicates whether cache settings were configured.</summary>
        public bool CacheConfigured { get; set; }
        /// <summary>True when an existing cache was marked for reuse.</summary>
        public bool CacheReused { get; set; }
        /// <summary>Human-readable error when the import could not proceed.</summary>
        public string? Error { get; set; }
    }
}
