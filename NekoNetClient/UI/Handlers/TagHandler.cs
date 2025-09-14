using NekoNetClient.Services.ServerConfiguration;

namespace NekoNetClient.UI.Handlers;

public class TagHandler
{
    public const string CustomAllTag = "Mare_All";
    public const string CustomOfflineTag = "Mare_Offline";
    public const string CustomOfflineSyncshellTag = "Mare_OfflineSyncshell";
    public const string CustomOnlineTag = "Mare_Online";
    public const string CustomUnpairedTag = "Mare_Unpaired";
    public const string CustomVisibleTag = "Mare_Visible";
    private readonly ServerConfigurationManager _serverConfigurationManager;
    private readonly string? _apiUrlOverride;

    public TagHandler(ServerConfigurationManager serverConfigurationManager)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _apiUrlOverride = null;
    }

    // Optional ctor for service-scoped handlers
    public TagHandler(ServerConfigurationManager serverConfigurationManager, string apiUrlOverride)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _apiUrlOverride = apiUrlOverride;
    }

    public void AddTag(string tag)
    {
        if (!string.IsNullOrEmpty(_apiUrlOverride))
            _serverConfigurationManager.AddTagForApiUrl(_apiUrlOverride!, tag);
        else
            _serverConfigurationManager.AddTag(tag);
    }

    public void AddTagToPairedUid(string uid, string tagName)
    {
        if (!string.IsNullOrEmpty(_apiUrlOverride))
            _serverConfigurationManager.AddTagForUidForApiUrl(_apiUrlOverride!, uid, tagName);
        else
            _serverConfigurationManager.AddTagForUid(uid, tagName);
    }

    public List<string> GetAllTagsSorted()
    {
        var tags = _apiUrlOverride == null
            ? _serverConfigurationManager.GetServerAvailablePairTags()
            : _serverConfigurationManager.GetServerAvailablePairTagsForApiUrl(_apiUrlOverride);
        return [.. tags.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];
    }

    public HashSet<string> GetOtherUidsForTag(string tag)
    {
        return _apiUrlOverride == null
            ? _serverConfigurationManager.GetUidsForTag(tag)
            : _serverConfigurationManager.GetUidsForTagForApiUrl(_apiUrlOverride, tag);
    }

    public bool HasAnyTag(string uid)
    {
        return _apiUrlOverride == null
            ? _serverConfigurationManager.HasTags(uid)
            : _serverConfigurationManager.HasTagsForApiUrl(_apiUrlOverride, uid);
    }

    public bool HasTag(string uid, string tagName)
    {
        return _apiUrlOverride == null
            ? _serverConfigurationManager.ContainsTag(uid, tagName)
            : _serverConfigurationManager.ContainsTagForApiUrl(_apiUrlOverride, uid, tagName);
    }

    /// <summary>
    /// Is this tag opened in the paired clients UI?
    /// </summary>
    /// <param name="tag">the tag</param>
    /// <returns>open true/false</returns>
    public bool IsTagOpen(string tag)
    {
        // Open/close UI state is tied to the current server context; for scoped handlers we keep it global.
        return _serverConfigurationManager.ContainsOpenPairTag(tag);
    }

    public void RemoveTag(string tag)
    {
        if (!string.IsNullOrEmpty(_apiUrlOverride))
            _serverConfigurationManager.RemoveTagForApiUrl(_apiUrlOverride!, tag);
        else
            _serverConfigurationManager.RemoveTag(tag);
    }

    public void RemoveTagFromPairedUid(string uid, string tagName)
    {
        if (!string.IsNullOrEmpty(_apiUrlOverride))
            _serverConfigurationManager.RemoveTagForUidForApiUrl(_apiUrlOverride!, uid, tagName);
        else
            _serverConfigurationManager.RemoveTagForUid(uid, tagName);
    }

    public void SetTagOpen(string tag, bool open)
    {
        if (open)
        {
            _serverConfigurationManager.AddOpenPairTag(tag);
        }
        else
        {
            _serverConfigurationManager.RemoveOpenPairTag(tag);
        }
    }
}
