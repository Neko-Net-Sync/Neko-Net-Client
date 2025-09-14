using System.Text.Json;
using NekoNetClient.MareConfiguration;
using NekoNetClient.MareConfiguration.Models;
using NekoNetClient.NekoHelpers.ImportModels;
using NekoNetClient.Services.ServerConfiguration;

namespace NekoNetClient.NekoHelpers;

public sealed class ImportWizard
{
    private readonly MareConfigService _config;
    private readonly ServerConfigurationManager _servers;

    public ImportWizard(MareConfigService config, ServerConfigurationManager servers)
    {
        _config  = config;
        _servers = servers;
    }

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
    public sealed class ScanResult
    {
        public string SourceFolder { get; set; } = "";
        public string? Error { get; set; }
        public bool FileCacheExists { get; set; }
        public string SuggestedCacheFolder { get; set; } = "";
        public int SourceCurrentIndex { get; set; }
        public MareConfigModel? SourceConfig { get; set; }
        public List<ServerRow> SourceServers { get; set; } = new();
    }

    public sealed class ServerRow
    {
        public bool Selected { get; set; }
        public string ServerName { get; set; } = "";
        public string ServerUri { get; set; } = "";
        public bool UseOAuth2 { get; set; }
        public bool HasToken { get; set; }
        public int AuthCount { get; set; }
        public MareServerEntry? Source { get; set; }
    }

    public sealed class ImportSummary
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public bool CacheConfigured { get; set; }
        public bool CacheReused { get; set; }
        public string? Error { get; set; }
    }
}
