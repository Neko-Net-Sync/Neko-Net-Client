/*
    Neko-Net Client — Services.ServerConfiguration.ServerConfigurationManager
    ------------------------------------------------------------------------
    Purpose
    - Central orchestrator for server configuration, credentials, per-server notes/tags, and API base routing.
    - Provides helpers for auth retrieval (OAuth/SecretKey), domain→endpoint mapping, and auto-populating notes.
*/
using Dalamud.Utility;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Logging;
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

namespace NekoNetClient.Services.ServerConfiguration;

/// <summary>
/// Central coordinator for server configuration, authentication, per-server notes/tags,
/// and API base routing. Provides convenience helpers for UI and service-scoped views.
/// </summary>
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

    /// <summary>
    /// Creates a new <see cref="ServerConfigurationManager"/>.
    /// Ensures the primary server entry exists.
    /// </summary>
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
    /// <summary>
    /// Maps a server URI's domain to the expected API endpoint base path.
    /// </summary>
    /// <param name="serverUri">Full server URI (ws/wss/http/https).</param>
    /// <returns>API base path starting with '/'.</returns>
    public string GetApiEndpointForDomain(string serverUri)
    {
        try
        {
            var uri = new Uri(serverUri);
            var domain = uri.Host.ToLowerInvariant();

            return domain switch
            {
                "connect.neko-net.cc" => "/mare",
                "sync.lightless-sync.org" => "/lightless",
                "tera.terasync.app" => "/tera-sync-v2",
                _ => "/mare" // Default endpoint
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse domain from {uri}, using default endpoint", serverUri);
            return "/mare";
        }
    }
    /// <summary>Gets the currently selected server API URL.</summary>
    public string CurrentApiUrl => CurrentServer.ServerUri;
    /// <summary>Gets the currently selected server configuration.</summary>
    public ServerStorage CurrentServer => _configService.Current.ServerStorage[CurrentServerIndex];
    /// <summary>
    /// Whether census/telemetry is enabled. Changing this value persists the setting.
    /// </summary>
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

    /// <summary>
    /// Tracks whether the census opt-in UI has been shown to the user.
    /// </summary>
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

    /// <summary>
    /// Gets or sets the current server index. Setting persists the selection.
    /// </summary>
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

    /// <summary>
    /// Retrieves the OAuth token and UID for the current character on the specified server index.
    /// Returns null if not configured or ambiguous. Sets <paramref name="hasMulti"/> when multiple
    /// identical character entries exist, prompting the UI to resolve the ambiguity.
    /// </summary>
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

    /// <summary>
    /// Retrieves the configured secret key for the current character on the specified server index.
    /// Returns null if not configured or ambiguous. Sets <paramref name="hasMulti"/> when multiple
    /// identical character entries exist.
    /// </summary>
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

    /// <summary>Returns the list of configured server API URLs.</summary>
    public string[] GetServerApiUrls()
    {
        return _configService.Current.ServerStorage.Select(v => v.ServerUri).ToArray();
    }

    // Expose servers for UI (read-only)
    /// <summary>Exposes all configured servers as a read-only list for UI.</summary>
    public IReadOnlyList<ServerStorage> GetAllServers() => _configService.Current.ServerStorage;

    /// <summary>Returns the number of configured servers.</summary>
    public int GetServerCount() => _configService.Current.ServerStorage.Count;

    // Server profiles UI removed; legacy storage retained in config but not used at runtime

    /// <summary>
    /// Returns the server at the given index. If the index is invalid, selects index 0
    /// and ensures the main server exists.
    /// </summary>
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

    /// <summary>
    /// Extracts the Discord user ID from the configured OAuth token for the given server.
    /// Returns empty string when not available and resets invalid tokens.
    /// </summary>
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

    /// <summary>Returns the configured server display names.</summary>
    public string[] GetServerNames()
    {
        return _configService.Current.ServerStorage.Select(v => v.ServerName).ToArray();
    }

    /// <summary>Checks if the current server has at least one authentication entry.</summary>
    public bool HasValidConfig()
    {
        return CurrentServer != null && CurrentServer.Authentications.Count > 0;
    }

    /// <summary>Persists configuration changes and logs the caller for diagnostics.</summary>
    public void Save()
    {
        var caller = new StackTrace().GetFrame(1)?.GetMethod()?.ReflectedType?.Name ?? "Unknown";
        _logger.LogDebug("{caller} Calling config save", caller);
        _configService.Save();
    }

    /// <summary>
    /// Selects the server at the given index, ensures API endpoint is populated based on host,
    /// clears FullPause, and persists changes.
    /// </summary>
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

    /// <summary>
    /// Adds the current Dalamud character as an authentication entry to the selected server index.
    /// No-op if an entry already exists.
    /// </summary>
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

    /// <summary>
    /// Adds an empty authentication entry to the server at the provided index.
    /// Used by UI to prepare selection before character details are known.
    /// </summary>
    internal void AddEmptyCharacterToServer(int serverSelectionIndex)
    {
        var server = GetServerByIndex(serverSelectionIndex);
        server.Authentications.Add(new Authentication()
        {
            SecretKeyIdx = server.SecretKeys.Any() ? server.SecretKeys.First().Key : -1,
        });
        Save();
    }

    /// <summary>Adds a tag to the Open Pair filter list for the current server.</summary>
    internal void AddOpenPairTag(string tag)
    {
        CurrentServerTagStorage().OpenPairTags.Add(tag);
        _serverTagConfig.Save();
    }

    /// <summary>Adds a new server to the configuration and persists.</summary>
    internal void AddServer(ServerStorage serverStorage)
    {
        _configService.Current.ServerStorage.Add(serverStorage);
        Save();
    }

    /// <summary>Adds a tag to the current server's available pair tags and refreshes the UI.</summary>
    internal void AddTag(string tag)
    {
        CurrentServerTagStorage().ServerAvailablePairTags.Add(tag);
        _serverTagConfig.Save();
        _mareMediator.Publish(new RefreshUiMessage());
    }

    // Per-API helpers for tags (used by per-service tabs)
    /// <summary>Per-API variant of <see cref="AddTag"/> used by multi-service views.</summary>
    internal void AddTagForApiUrl(string apiUrl, string tag)
    {
        var storage = ServerTagStorageForUrl(apiUrl);
        storage.ServerAvailablePairTags.Add(tag);
        _serverTagConfig.Save();
        _mareMediator.Publish(new RefreshUiMessage());
    }

    /// <summary>Adds a tag to a specific UID on the current server and refreshes UI.</summary>
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

    /// <summary>Per-API variant of <see cref="AddTagForUid"/> used by service-scoped views.</summary>
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

    /// <summary>Returns true if the given tag is in the Open Pair filter list.</summary>
    internal bool ContainsOpenPairTag(string tag)
    {
        return CurrentServerTagStorage().OpenPairTags.Contains(tag);
    }

    /// <summary>Returns true if the UID has the specified tag on the current server.</summary>
    internal bool ContainsTag(string uid, string tag)
    {
        if (CurrentServerTagStorage().UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            return tags.Contains(tag, StringComparer.Ordinal);
        }

        return false;
    }

    /// <summary>Removes the server from configuration and adjusts the selected index if needed.</summary>
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

    /// <summary>Gets the note for a group ID on the current server, if any.</summary>
    internal string? GetNoteForGid(string gID)
    {
        if (CurrentNotesStorage().GidServerComments.TryGetValue(gID, out var note))
        {
            if (string.IsNullOrEmpty(note)) return null;
            return note;
        }

        return null;
    }

    /// <summary>Gets the note for a UID on the current server, if any.</summary>
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
    /// <summary>Per-URL variant of <see cref="GetNoteForUid"/> for multi-service views.</summary>
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
    /// <summary>Per-URL setter for UID note used by service-scoped views.</summary>
    internal void SetNoteForUidForApiUrl(string apiUrl, string uid, string note, bool save = true)
    {
        if (string.IsNullOrEmpty(uid)) return;
        var storage = ServerNotesStorageForUrl(apiUrl);
        storage.UidServerComments[uid] = note;
        if (save) _notesConfig.Save();
    }

    /// <summary>Gets the available pair tags for the current server.</summary>
    internal HashSet<string> GetServerAvailablePairTags()
    {
        return CurrentServerTagStorage().ServerAvailablePairTags;
    }

    /// <summary>Per-URL variant of <see cref="GetServerAvailablePairTags"/>.</summary>
    internal HashSet<string> GetServerAvailablePairTagsForApiUrl(string apiUrl)
    {
        return ServerTagStorageForUrl(apiUrl).ServerAvailablePairTags;
    }

    /// <summary>Gets the UID → tags mapping for the current server.</summary>
    internal Dictionary<string, List<string>> GetUidServerPairedUserTags()
    {
        return CurrentServerTagStorage().UidServerPairedUserTags;
    }

    /// <summary>Per-URL variant of <see cref="GetUidServerPairedUserTags"/>.</summary>
    internal Dictionary<string, List<string>> GetUidServerPairedUserTagsForApiUrl(string apiUrl)
    {
        return ServerTagStorageForUrl(apiUrl).UidServerPairedUserTags;
    }

    /// <summary>Gets the set of UIDs that have the given tag on the current server.</summary>
    internal HashSet<string> GetUidsForTag(string tag)
    {
        return CurrentServerTagStorage().UidServerPairedUserTags.Where(p => p.Value.Contains(tag, StringComparer.Ordinal)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Per-URL variant of <see cref="GetUidsForTag"/>.</summary>
    internal HashSet<string> GetUidsForTagForApiUrl(string apiUrl, string tag)
    {
        var storage = ServerTagStorageForUrl(apiUrl);
        return storage.UidServerPairedUserTags.Where(p => p.Value.Contains(tag, StringComparer.Ordinal)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Returns true if the UID has any tags on the current server.</summary>
    internal bool HasTags(string uid)
    {
        if (CurrentServerTagStorage().UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            return tags.Any();
        }

        return false;
    }

    /// <summary>Per-URL variant of <see cref="HasTags"/>.</summary>
    internal bool HasTagsForApiUrl(string apiUrl, string uid)
    {
        var storage = ServerTagStorageForUrl(apiUrl);
        if (storage.UidServerPairedUserTags.TryGetValue(uid, out var tags))
        {
            return tags.Any();
        }
        return false;
    }

    /// <summary>Per-URL check whether a UID has a given tag.</summary>
    internal bool ContainsTagForApiUrl(string apiUrl, string uid, string tagName)
    {
        var storage = ServerTagStorageForUrl(apiUrl);
        return storage.UidServerPairedUserTags.TryGetValue(uid, out var tags) && tags.Contains(tagName, StringComparer.Ordinal);
    }

    /// <summary>Removes the given authentication entry from the server at index.</summary>
    internal void RemoveCharacterFromServer(int serverSelectionIndex, Authentication item)
    {
        var server = GetServerByIndex(serverSelectionIndex);
        server.Authentications.Remove(item);
        Save();
    }

    /// <summary>Removes a tag from the Open Pair filter list.</summary>
    internal void RemoveOpenPairTag(string tag)
    {
        CurrentServerTagStorage().OpenPairTags.Remove(tag);
        _serverTagConfig.Save();
    }

    /// <summary>Removes a tag globally and from all UIDs on the current server.</summary>
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

    /// <summary>Per-URL variant of <see cref="RemoveTag"/>.</summary>
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

    /// <summary>Removes a tag from a specific UID on the current server.</summary>
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

    /// <summary>Per-URL variant of <see cref="RemoveTagForUid"/>.</summary>
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

    /// <summary>Renames a tag across the current server and updates all UID mappings.</summary>
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

    /// <summary>Persists note changes to disk.</summary>
    internal void SaveNotes()
    {
        _notesConfig.Save();
    }

    /// <summary>Sets the note for a group on the current server.</summary>
    internal void SetNoteForGid(string gid, string note, bool save = true)
    {
        if (string.IsNullOrEmpty(gid)) return;

        CurrentNotesStorage().GidServerComments[gid] = note;
        if (save)
            _notesConfig.Save();
    }

    /// <summary>Sets the note for a user on the current server.</summary>
    internal void SetNoteForUid(string uid, string note, bool save = true)
    {
        if (string.IsNullOrEmpty(uid)) return;

        CurrentNotesStorage().UidServerComments[uid] = note;
        if (save)
            _notesConfig.Save();
    }

    /// <summary>
    /// Automatically populates an empty note for a UID from character name when enabled in settings.
    /// No-op when a note already exists.
    /// </summary>
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

    /// <summary>
    /// Queries the server for the mapping of UID → Discord user using the provided OAuth token.
    /// </summary>
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

    /// <summary>
    /// Checks whether the server supports Discord OAuth and returns the authorization endpoint when available.
    /// </summary>
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

    /// <summary>
    /// Opens the browser for Discord auth and polls the server for the resulting token.
    /// Returns null on timeout or error.
    /// </summary>
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

    /// <summary>Gets the preferred HTTP transport for SignalR connections.</summary>
    public HttpTransportType GetTransport()
    {
        return CurrentServer.HttpTransportType;
    }

    /// <summary>Sets the preferred HTTP transport for SignalR and persists the change.</summary>
    public void SetTransportType(HttpTransportType httpTransportType)
    {
        CurrentServer.HttpTransportType = httpTransportType;
        Save();
    }
}
