using Dalamud.Utility;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Logging;
using NekoNet.API.Data.Enum;
using NekoNet.API.Routes;
using NekoNetClient.MareConfiguration;
using NekoNetClient.MareConfiguration.Models;
using NekoNetClient.Services.Mediator;
using NekoNetClient.WebAPI.SignalR;
using Serilog.Core;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;

namespace NekoNetClient.Services.ServerConfiguration;

public class ServerConfigurationManager
{
    private readonly ServerConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly MareConfigService _mareConfigService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ServerConfigurationManager> _logger;
    private readonly MareMediator _mareMediator;
    private readonly NotesConfigService _notesConfig;
    private readonly ServerTagConfigService _serverTagConfig;

    public ServerConfigurationManager(ILogger<ServerConfigurationManager> logger, ServerConfigService configService,
        ServerTagConfigService serverTagConfig, NotesConfigService notesConfig, DalamudUtilService dalamudUtil,
        MareConfigService mareConfigService, HttpClient httpClient, MareMediator mareMediator)
    {
        _logger = logger;
        _configService = configService;
        _serverTagConfig = serverTagConfig;
        _notesConfig = notesConfig;
        _dalamudUtil = dalamudUtil;
        _mareConfigService = mareConfigService;
        _httpClient = httpClient;
        _mareMediator = mareMediator;
        EnsureMainExists();
    }
    public string GetApiEndpointForDomain(string serverUri)
    {
        try
        {
            if (SyncServiceSpecifications.TryResolveServiceByHost(serverUri, out var service) &&
                SyncServiceSpecifications.TryGet(service, out var specification))
            {
                return specification.ApiPath;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse domain from {uri}, using default endpoint", serverUri);
        }

        return "/mare";
    }
    public string CurrentApiUrl => CurrentServer.ServerUri;
    public ServerStorage CurrentServer => _configService.Current.ServerStorage[CurrentServerIndex];
    public bool SendCensusData
    {
        get
        {
            return _configService.Current.SendCensusData;
        }
        set
        {
            _configService.Current.SendCensusData = value;
            _configService.Save();
        }
    }

    public bool ShownCensusPopup
    {
        get
        {
            return _configService.Current.ShownCensusPopup;
        }
        set
        {
            _configService.Current.ShownCensusPopup = value;
            _configService.Save();
        }
    }

    public int CurrentServerIndex
    {
        set
        {
            _configService.Current.CurrentServer = value;
            _configService.Save();
        }
        get
        {
            if (_configService.Current.CurrentServer < 0)
            {
                _configService.Current.CurrentServer = 0;
                _configService.Save();
            }

            return _configService.Current.CurrentServer;
        }
    }

    public (string OAuthToken, string UID)? GetOAuth2(out bool hasMulti, int serverIdx = -1)
    {
        ServerStorage? currentServer;
        currentServer = serverIdx == -1 ? CurrentServer : GetServerByIndex(serverIdx);
        if (currentServer == null)
        {
            currentServer = new();
            Save();
        }
        hasMulti = false;

        var charaName = _dalamudUtil.GetPlayerNameAsync().GetAwaiter().GetResult();
        var worldId = _dalamudUtil.GetHomeWorldIdAsync().GetAwaiter().GetResult();
        var cid = _dalamudUtil.GetCIDAsync().GetAwaiter().GetResult();

        var auth = currentServer.Authentications.FindAll(f => string.Equals(f.CharacterName, charaName) && f.WorldId == worldId);
        if (auth.Count >= 2)
        {
            _logger.LogTrace("GetOAuth2 accessed, returning null because multiple ({count}) identical characters.", auth.Count);
            hasMulti = true;
            return null;
        }

        if (auth.Count == 0)
        {
            _logger.LogTrace("GetOAuth2 accessed, returning null because no set up characters for {chara} on {world}", charaName, worldId);
            return null;
        }

        if (auth.Single().LastSeenCID != cid)
        {
            auth.Single().LastSeenCID = cid;
            _logger.LogTrace("GetOAuth2 accessed, updating CID for {chara} on {world} to {cid}", charaName, worldId, cid);
            Save();
        }

        if (!string.IsNullOrEmpty(auth.Single().UID) && !string.IsNullOrEmpty(currentServer.OAuthToken))
        {
            _logger.LogTrace("GetOAuth2 accessed, returning {key} ({keyValue}) for {chara} on {world}", auth.Single().UID, string.Join("", currentServer.OAuthToken.Take(10)), charaName, worldId);
            return (currentServer.OAuthToken, auth.Single().UID!);
        }

        _logger.LogTrace("GetOAuth2 accessed, returning null because no UID found for {chara} on {world} or OAuthToken is not configured.", charaName, worldId);

        return null;
    }

    public string? GetSecretKey(out bool hasMulti, int serverIdx = -1)
    {
        ServerStorage? currentServer;
        currentServer = serverIdx == -1 ? CurrentServer : GetServerByIndex(serverIdx);
        if (currentServer == null)
        {
            currentServer = new();
            Save();
        }
        hasMulti = false;

        var charaName = _dalamudUtil.GetPlayerNameAsync().GetAwaiter().GetResult();
        var worldId = _dalamudUtil.GetHomeWorldIdAsync().GetAwaiter().GetResult();
        var cid = _dalamudUtil.GetCIDAsync().GetAwaiter().GetResult();
        if (!currentServer.Authentications.Any() && currentServer.SecretKeys.Any())
        {
            currentServer.Authentications.Add(new Authentication()
            {
                CharacterName = charaName,
                WorldId = worldId,
                LastSeenCID = cid,
                SecretKeyIdx = currentServer.SecretKeys.Last().Key,
            });

            Save();
        }

        var auth = currentServer.Authentications.FindAll(f => string.Equals(f.CharacterName, charaName, StringComparison.Ordinal) && f.WorldId == worldId);
        if (auth.Count >= 2)
        {
            _logger.LogTrace("GetSecretKey accessed, returning null because multiple ({count}) identical characters.", auth.Count);
            hasMulti = true;
            return null;
        }

        if (auth.Count == 0)
        {
            _logger.LogTrace("GetSecretKey accessed, returning null because no set up characters for {chara} on {world}", charaName, worldId);
            return null;
        }

        if (auth.Single().LastSeenCID != cid)
        {
            auth.Single().LastSeenCID = cid;
            _logger.LogTrace("GetSecretKey accessed, updating CID for {chara} on {world} to {cid}", charaName, worldId, cid);
            Save();
        }

        if (currentServer.SecretKeys.TryGetValue(auth.Single().SecretKeyIdx, out var secretKey))
        {
            _logger.LogTrace("GetSecretKey accessed, returning {key} ({keyValue}) for {chara} on {world}", secretKey.FriendlyName, string.Join("", secretKey.Key.Take(10)), charaName, worldId);
            return secretKey.Key;
        }

        _logger.LogTrace("GetSecretKey accessed, returning null because no fitting key found for {chara} on {world} for idx {idx}.", charaName, worldId, auth.Single().SecretKeyIdx);

        return null;
    }

    public string[] GetServerApiUrls()
    {
        return _configService.Current.ServerStorage.Select(v => v.ServerUri).ToArray();
    }

    public int? FindServerIndexByHost(string? hostOrUrl)
    {
        if (!SyncServiceSpecifications.TryResolveServiceByHost(hostOrUrl, out var service))
        {
            return null;
        }

        return FindServerIndexByService(service);
    }

    public int? FindServerIndexByService(SyncService service)
    {
        if (!SyncServiceSpecifications.TryGet(service, out var specification))
        {
            return null;
        }

        for (var i = 0; i < _configService.Current.ServerStorage.Count; i++)
        {
            var server = _configService.Current.ServerStorage[i];
            if (IsMatch(server, specification))
            {
                return i;
            }
        }

        return null;
    }

    private bool IsMatch(ServerStorage server, SyncServiceSpecifications.Specification specification)
    {
        if (string.IsNullOrWhiteSpace(server.ServerUri))
        {
            return false;
        }

        var host = SyncServiceSpecifications.NormalizeHostOrAuthority(server.ServerUri);
        if (host != null && specification.Hosts.Contains(host))
        {
            return true;
        }

        var configuredPath = ResolveConfiguredApiPath(server);
        return !string.IsNullOrEmpty(configuredPath) &&
               string.Equals(configuredPath, specification.ApiPath, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveConfiguredApiPath(ServerStorage server)
    {
        if (!string.IsNullOrWhiteSpace(server.ApiEndpoint))
        {
            return SyncServiceSpecifications.NormalizePath(server.ApiEndpoint);
        }

        if (Uri.TryCreate(server.ServerUri, UriKind.Absolute, out var uri))
        {
            var absolute = SyncServiceSpecifications.NormalizePath(uri.AbsolutePath);
            if (!string.IsNullOrEmpty(absolute))
            {
                return absolute;
            }
        }

        return SyncServiceSpecifications.NormalizePath(GetApiEndpointForDomain(server.ServerUri));
    }

    // Expose servers for UI (read-only)
    public IReadOnlyList<ServerStorage> GetAllServers() => _configService.Current.ServerStorage;

    public int GetServerCount() => _configService.Current.ServerStorage.Count;

    // Server profiles UI removed; legacy storage retained in config but not used at runtime

    public ServerStorage GetServerByIndex(int idx)
    {
        try
        {
            return _configService.Current.ServerStorage[idx];
        }
        catch
        {
            _configService.Current.CurrentServer = 0;
            EnsureMainExists();
            return CurrentServer!;
        }
    }

    public string GetDiscordUserFromToken(ServerStorage server)
    {
        JwtSecurityTokenHandler handler = new JwtSecurityTokenHandler();
        if (string.IsNullOrEmpty(server.OAuthToken)) return string.Empty;
        try
        {
            var token = handler.ReadJwtToken(server.OAuthToken);
            return token.Claims.First(f => string.Equals(f.Type, "discord_user", StringComparison.Ordinal)).Value!;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read jwt, resetting it");
            server.OAuthToken = null;
            Save();
            return string.Empty;
        }
    }

    public string[] GetServerNames()
    {
        return _configService.Current.ServerStorage.Select(v => v.ServerName).ToArray();
    }

    public bool HasValidConfig()
    {
        return CurrentServer != null && CurrentServer.Authentications.Count > 0;
    }

    public void Save()
    {
        var caller = new StackTrace().GetFrame(1)?.GetMethod()?.ReflectedType?.Name ?? "Unknown";
        _logger.LogDebug("{caller} Calling config save", caller);
        _configService.Save();
    }

    public void SelectServer(int idx)
    {
        _configService.Current.CurrentServer = idx;
        CurrentServer!.FullPause = false;
        // Ensure API endpoint is set for the selected host
        try
        {
            var sel = CurrentServer!;
            if (string.IsNullOrWhiteSpace(sel.ApiEndpoint))
            {
                sel.ApiEndpoint = GetApiEndpointForDomain(sel.ServerUri);
            }
        }
        catch { /* ignore */ }
        Save();
    }

    internal void AddCurrentCharacterToServer(int serverSelectionIndex = -1)
    {
        if (serverSelectionIndex == -1) serverSelectionIndex = CurrentServerIndex;
        var server = GetServerByIndex(serverSelectionIndex);
        if (server.Authentications.Any(c => string.Equals(c.CharacterName, _dalamudUtil.GetPlayerNameAsync().GetAwaiter().GetResult(), StringComparison.Ordinal)
                && c.WorldId == _dalamudUtil.GetHomeWorldIdAsync().GetAwaiter().GetResult()))
            return;

        server.Authentications.Add(new Authentication()
        {
            CharacterName = _dalamudUtil.GetPlayerNameAsync().GetAwaiter().GetResult(),
            WorldId = _dalamudUtil.GetHomeWorldIdAsync().GetAwaiter().GetResult(),
            SecretKeyIdx = !server.UseOAuth2 ? server.SecretKeys.Last().Key : -1,
            LastSeenCID = _dalamudUtil.GetCIDAsync().GetAwaiter().GetResult()
        });
        Save();
    }

    internal void AddEmptyCharacterToServer(int serverSelectionIndex)
    {
        var server = GetServerByIndex(serverSelectionIndex);
        server.Authentications.Add(new Authentication()
        {
            SecretKeyIdx = server.SecretKeys.Any() ? server.SecretKeys.First().Key : -1,
        });
        Save();
    }

    internal void AddOpenPairTag(string tag)
    {
        CurrentServerTagStorage().OpenPairTags.Add(tag);
        _serverTagConfig.Save();
    }

    internal void AddServer(ServerStorage serverStorage)
    {
        _configService.Current.ServerStorage.Add(serverStorage);
        Save();
    }

    internal void AddTag(string tag)
    {
        CurrentServerTagStorage().ServerAvailablePairTags.Add(tag);
        _serverTagConfig.Save();
        _mareMediator.Publish(new RefreshUiMessage());
    }

    // Per-API helpers for tags (used by per-service tabs)
    internal void AddTagForApiUrl(string apiUrl, string tag)
    {
        var storage = ServerTagStorageForUrl(apiUrl);
        storage.ServerAvailablePairTags.Add(tag);
        _serverTagConfig.Save();
        _mareMediator.Publish(new RefreshUiMessage());
    }

    internal void AddTagForUid(string uid, string tagName)
    {
        if (CurrentServerTagStorage().UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            tags.Add(tagName);
            _mareMediator.Publish(new RefreshUiMessage());
        }
        else
        {
            CurrentServerTagStorage().UidServerPairedUserTags[uid] = [tagName];
        }

        _serverTagConfig.Save();
    }

    internal void AddTagForUidForApiUrl(string apiUrl, string uid, string tagName, bool save = true)
    {
        var storage = ServerTagStorageForUrl(apiUrl);
        if (storage.UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            tags.Add(tagName);
            _mareMediator.Publish(new RefreshUiMessage());
        }
        else
        {
            storage.UidServerPairedUserTags[uid] = new List<string>(new[] { tagName });
        }

        if (save) _serverTagConfig.Save();
    }

    internal bool ContainsOpenPairTag(string tag)
    {
        return CurrentServerTagStorage().OpenPairTags.Contains(tag);
    }

    internal bool ContainsTag(string uid, string tag)
    {
        if (CurrentServerTagStorage().UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            return tags.Contains(tag, StringComparer.Ordinal);
        }

        return false;
    }

    internal void DeleteServer(ServerStorage selectedServer)
    {
        if (Array.IndexOf(_configService.Current.ServerStorage.ToArray(), selectedServer) <
            _configService.Current.CurrentServer)
        {
            _configService.Current.CurrentServer--;
        }

        _configService.Current.ServerStorage.Remove(selectedServer);
        Save();
    }

    internal string? GetNoteForGid(string gID)
    {
        if (CurrentNotesStorage().GidServerComments.TryGetValue(gID, out var note))
        {
            if (string.IsNullOrEmpty(note)) return null;
            return note;
        }

        return null;
    }

    internal string? GetNoteForUid(string uid)
    {
        if (CurrentNotesStorage().UidServerComments.TryGetValue(uid, out var note))
        {
            if (string.IsNullOrEmpty(note)) return null;
            return note;
        }
        return null;
    }

    // Per-URL variants used for multi-service views
    internal string? GetNoteForUidForApiUrl(string apiUrl, string uid)
    {
        var storage = ServerNotesStorageForUrl(apiUrl);
        if (storage.UidServerComments.TryGetValue(uid, out var note))
        {
            if (string.IsNullOrEmpty(note)) return null;
            return note;
        }
        return null;
    }

    // Per-URL setter used by service-scoped views/rows
    internal void SetNoteForUidForApiUrl(string apiUrl, string uid, string note, bool save = true)
    {
        if (string.IsNullOrEmpty(uid)) return;
        var storage = ServerNotesStorageForUrl(apiUrl);
        storage.UidServerComments[uid] = note;
        if (save) _notesConfig.Save();
    }

    internal HashSet<string> GetServerAvailablePairTags()
    {
        return CurrentServerTagStorage().ServerAvailablePairTags;
    }

    internal HashSet<string> GetServerAvailablePairTagsForApiUrl(string apiUrl)
    {
        return ServerTagStorageForUrl(apiUrl).ServerAvailablePairTags;
    }

    internal Dictionary<string, List<string>> GetUidServerPairedUserTags()
    {
        return CurrentServerTagStorage().UidServerPairedUserTags;
    }

    internal Dictionary<string, List<string>> GetUidServerPairedUserTagsForApiUrl(string apiUrl)
    {
        return ServerTagStorageForUrl(apiUrl).UidServerPairedUserTags;
    }

    internal HashSet<string> GetUidsForTag(string tag)
    {
        return CurrentServerTagStorage().UidServerPairedUserTags.Where(p => p.Value.Contains(tag, StringComparer.Ordinal)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
    }

    internal HashSet<string> GetUidsForTagForApiUrl(string apiUrl, string tag)
    {
        var storage = ServerTagStorageForUrl(apiUrl);
        return storage.UidServerPairedUserTags.Where(p => p.Value.Contains(tag, StringComparer.Ordinal)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
    }

    internal bool HasTags(string uid)
    {
        if (CurrentServerTagStorage().UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            return tags.Any();
        }

        return false;
    }

    internal bool HasTagsForApiUrl(string apiUrl, string uid)
    {
        var storage = ServerTagStorageForUrl(apiUrl);
        if (storage.UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            return tags.Any();
        }
        return false;
    }

    internal bool ContainsTagForApiUrl(string apiUrl, string uid, string tagName)
    {
        var storage = ServerTagStorageForUrl(apiUrl);
        return storage.UidServerPairedUserTags.TryGetValue(uid, out var tags) && tags.Contains(tagName, StringComparer.Ordinal);
    }

    internal void RemoveCharacterFromServer(int serverSelectionIndex, Authentication item)
    {
        var server = GetServerByIndex(serverSelectionIndex);
        server.Authentications.Remove(item);
        Save();
    }

    internal void RemoveOpenPairTag(string tag)
    {
        CurrentServerTagStorage().OpenPairTags.Remove(tag);
        _serverTagConfig.Save();
    }

    internal void RemoveTag(string tag)
    {
        CurrentServerTagStorage().ServerAvailablePairTags.Remove(tag);
        foreach (var uid in GetUidsForTag(tag))
        {
            RemoveTagForUid(uid, tag, save: false);
        }
        _serverTagConfig.Save();
        _mareMediator.Publish(new RefreshUiMessage());
    }

    internal void RemoveTagForApiUrl(string apiUrl, string tag)
    {
        var storage = ServerTagStorageForUrl(apiUrl);
        storage.ServerAvailablePairTags.Remove(tag);
        foreach (var kv in storage.UidServerPairedUserTags.ToList())
        {
            kv.Value.Remove(tag);
        }
        _serverTagConfig.Save();
        _mareMediator.Publish(new RefreshUiMessage());
    }

    internal void RemoveTagForUid(string uid, string tagName, bool save = true)
    {
        if (CurrentServerTagStorage().UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            tags.Remove(tagName);

            if (save)
            {
                _serverTagConfig.Save();
                _mareMediator.Publish(new RefreshUiMessage());
            }
        }
    }

    internal void RemoveTagForUidForApiUrl(string apiUrl, string uid, string tagName, bool save = true)
    {
        var storage = ServerTagStorageForUrl(apiUrl);
        if (storage.UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            tags.Remove(tagName);
            if (save)
            {
                _serverTagConfig.Save();
                _mareMediator.Publish(new RefreshUiMessage());
            }
        }
    }

    internal void RenameTag(string oldName, string newName)
    {
        CurrentServerTagStorage().ServerAvailablePairTags.Remove(oldName);
        CurrentServerTagStorage().ServerAvailablePairTags.Add(newName);
        foreach (var existingTags in CurrentServerTagStorage().UidServerPairedUserTags.Select(k => k.Value))
        {
            if (existingTags.Remove(oldName))
                existingTags.Add(newName);
        }
    }

    internal void SaveNotes()
    {
        _notesConfig.Save();
    }

    internal void SetNoteForGid(string gid, string note, bool save = true)
    {
        if (string.IsNullOrEmpty(gid)) return;

        CurrentNotesStorage().GidServerComments[gid] = note;
        if (save)
            _notesConfig.Save();
    }

    internal void SetNoteForUid(string uid, string note, bool save = true)
    {
        if (string.IsNullOrEmpty(uid)) return;

        CurrentNotesStorage().UidServerComments[uid] = note;
        if (save)
            _notesConfig.Save();
    }

    internal void AutoPopulateNoteForUid(string uid, string note)
    {
        if (!_mareConfigService.Current.AutoPopulateEmptyNotesFromCharaName
            || GetNoteForUid(uid) != null)
            return;

        SetNoteForUid(uid, note, save: true);
    }

    private ServerNotesStorage CurrentNotesStorage()
    {
        TryCreateCurrentNotesStorage();
        return _notesConfig.Current.ServerNotes[CurrentApiUrl];
    }

    private ServerNotesStorage ServerNotesStorageForUrl(string apiUrl)
    {
        if (!_notesConfig.Current.ServerNotes.ContainsKey(apiUrl))
        {
            _notesConfig.Current.ServerNotes[apiUrl] = new();
        }
        return _notesConfig.Current.ServerNotes[apiUrl];
    }

    private ServerTagStorage CurrentServerTagStorage()
    {
        TryCreateCurrentServerTagStorage();
        return _serverTagConfig.Current.ServerTagStorage[CurrentApiUrl];
    }

    private void EnsureMainExists()
    {
        if (_configService.Current.ServerStorage.Count == 0 || !string.Equals(_configService.Current.ServerStorage[0].ServerUri, ApiController.MainServiceUri, StringComparison.OrdinalIgnoreCase))
        {
            _configService.Current.ServerStorage.Insert(0, new ServerStorage() { ServerUri = ApiController.MainServiceUri, ServerName = ApiController.MainServer, UseOAuth2 = true });
        }
        Save();
    }

    private void TryCreateCurrentNotesStorage()
    {
        if (!_notesConfig.Current.ServerNotes.ContainsKey(CurrentApiUrl))
        {
            _notesConfig.Current.ServerNotes[CurrentApiUrl] = new();
        }
    }

    private void TryCreateCurrentServerTagStorage()
    {
        if (!_serverTagConfig.Current.ServerTagStorage.ContainsKey(CurrentApiUrl))
        {
            _serverTagConfig.Current.ServerTagStorage[CurrentApiUrl] = new();
        }
    }

    private ServerTagStorage ServerTagStorageForUrl(string apiUrl)
    {
        if (!_serverTagConfig.Current.ServerTagStorage.ContainsKey(apiUrl))
        {
            _serverTagConfig.Current.ServerTagStorage[apiUrl] = new();
        }
        return _serverTagConfig.Current.ServerTagStorage[apiUrl];
    }

    public async Task<Dictionary<string, string>> GetUIDsWithDiscordToken(string serverUri, string token)
    {
        try
        {
            var baseUri = serverUri.Replace("wss://", "https://").Replace("ws://", "http://");
            var oauthCheckUri = MareAuth.GetUIDsFullPath(new Uri(baseUri));
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var response = await _httpClient.GetAsync(oauthCheckUri).ConfigureAwait(false);
            var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(responseStream).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failure getting UIDs");
            return [];
        }
    }

    public async Task<Uri?> CheckDiscordOAuth(string serverUri)
    {
        try
        {
            var baseUri = serverUri.Replace("wss://", "https://").Replace("ws://", "http://");
            var oauthCheckUri = MareAuth.GetDiscordOAuthEndpointFullPath(new Uri(baseUri));
            var response = await _httpClient.GetFromJsonAsync<Uri?>(oauthCheckUri).ConfigureAwait(false);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failure checking for Discord Auth");
            return null;
        }
    }

    public async Task<string?> GetDiscordOAuthToken(Uri discordAuthUri, string serverUri, CancellationToken token)
    {
        var sessionId = BitConverter.ToString(RandomNumberGenerator.GetBytes(64)).Replace("-", "").ToLower();
        Util.OpenLink(discordAuthUri.ToString() + "?sessionId=" + sessionId);

        string? discordToken = null;
        using CancellationTokenSource timeOutCts = new();
        timeOutCts.CancelAfter(TimeSpan.FromSeconds(60));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeOutCts.Token, token);
        try
        {
            var baseUri = serverUri.Replace("wss://", "https://").Replace("ws://", "http://");
            var oauthCheckUri = MareAuth.GetDiscordOAuthTokenFullPath(new Uri(baseUri), sessionId);
            var response = await _httpClient.GetAsync(oauthCheckUri, linkedCts.Token).ConfigureAwait(false);
            discordToken = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failure getting Discord Token");
            return null;
        }

        if (discordToken == null)
            return null;

        return discordToken;
    }

    public HttpTransportType GetTransport()
    {
        return CurrentServer.HttpTransportType;
    }

    public void SetTransportType(HttpTransportType httpTransportType)
    {
        CurrentServer.HttpTransportType = httpTransportType;
        Save();
    }
}
