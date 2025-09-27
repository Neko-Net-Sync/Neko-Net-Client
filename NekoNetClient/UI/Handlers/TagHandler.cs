/*
     Neko-Net Client — UI.Handlers.TagHandler
     ----------------------------------------
     Purpose
     - Thin wrapper around ServerConfigurationManager to manage pair tags and open state, optionally scoped
         to a specific service API URL for multi-service contexts.
*/
using NekoNetClient.Services.ServerConfiguration;

namespace NekoNetClient.UI.Handlers;

/// <summary>
/// Provides a UI-facing API for manipulating tags associated with pairs, with optional service scoping.
/// </summary>
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

    /// <summary>
    /// Creates a global tag handler (no service scoping).
    /// </summary>
    public TagHandler(ServerConfigurationManager serverConfigurationManager)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _apiUrlOverride = null;
    }

    // Optional ctor for service-scoped handlers
    /// <summary>
    /// Creates a service-scoped tag handler that routes operations to the specified API URL context.
    /// </summary>
    public TagHandler(ServerConfigurationManager serverConfigurationManager, string apiUrlOverride)
    {
        _serverConfigurationManager = serverConfigurationManager;
        _apiUrlOverride = apiUrlOverride;
    }

    /// <summary>
    /// Adds a tag to the available set.
    /// </summary>
    public void AddTag(string tag)
    {
        if (!string.IsNullOrEmpty(_apiUrlOverride))
            _serverConfigurationManager.AddTagForApiUrl(_apiUrlOverride!, tag);
        else
            _serverConfigurationManager.AddTag(tag);
    }

    /// <summary>
    /// Associates a tag with a paired UID.
    /// </summary>
    public void AddTagToPairedUid(string uid, string tagName)
    {
        if (!string.IsNullOrEmpty(_apiUrlOverride))
            _serverConfigurationManager.AddTagForUidForApiUrl(_apiUrlOverride!, uid, tagName);
        else
            _serverConfigurationManager.AddTagForUid(uid, tagName);
    }

    /// <summary>
    /// Returns all available tags in case-insensitive sorted order.
    /// </summary>
    public List<string> GetAllTagsSorted()
    {
        var tags = _apiUrlOverride == null
            ? _serverConfigurationManager.GetServerAvailablePairTags()
            : _serverConfigurationManager.GetServerAvailablePairTagsForApiUrl(_apiUrlOverride);
        return [.. tags.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Returns the set of UIDs that have the specified tag.
    /// </summary>
    public HashSet<string> GetOtherUidsForTag(string tag)
    {
        return _apiUrlOverride == null
            ? _serverConfigurationManager.GetUidsForTag(tag)
            : _serverConfigurationManager.GetUidsForTagForApiUrl(_apiUrlOverride, tag);
    }

    /// <summary>
    /// Whether the specified UID has any tag at all.
    /// </summary>
    public bool HasAnyTag(string uid)
    {
        return _apiUrlOverride == null
            ? _serverConfigurationManager.HasTags(uid)
            : _serverConfigurationManager.HasTagsForApiUrl(_apiUrlOverride, uid);
    }

    /// <summary>
    /// Whether the specified UID has a specific tag.
    /// </summary>
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

    /// <summary>
    /// Removes a tag from the available set.
    /// </summary>
    public void RemoveTag(string tag)
    {
        if (!string.IsNullOrEmpty(_apiUrlOverride))
            _serverConfigurationManager.RemoveTagForApiUrl(_apiUrlOverride!, tag);
        else
            _serverConfigurationManager.RemoveTag(tag);
    }

    /// <summary>
    /// Disassociates a tag from a specific UID.
    /// </summary>
    public void RemoveTagFromPairedUid(string uid, string tagName)
    {
        if (!string.IsNullOrEmpty(_apiUrlOverride))
            _serverConfigurationManager.RemoveTagForUidForApiUrl(_apiUrlOverride!, uid, tagName);
        else
            _serverConfigurationManager.RemoveTagForUid(uid, tagName);
    }

    /// <summary>
    /// Marks a tag as open or closed in the UI.
    /// </summary>
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
