// -------------------------------------------------------------------------------------------------
// Neko-Net Client — NekoHelpers.ImportModels
//
// Purpose
//   Minimal POCOs mirroring the JSON structure used by other plugins' configuration files
//   (config.json and server.json). These are intentionally small and only include the properties
//   required for import; they are not used by runtime systems beyond deserialization in the
//   import helpers.
// -------------------------------------------------------------------------------------------------
namespace NekoNetClient.NekoHelpers.ImportModels;

/// <summary>
/// Mirror of a typical plugin's config.json used for importing cache-related settings.
/// </summary>
public sealed class MareConfigModel
{
    /// <summary>Whether the user has accepted the plugin's agreement in the source config.</summary>
    public bool AcceptedAgreement { get; set; }
    /// <summary>Absolute path to the cache folder, when present.</summary>
    public string? CacheFolder { get; set; }
    /// <summary>Indicates the source client completed its initial scan.</summary>
    public bool InitialScanComplete { get; set; }
    /// <summary>Whether the source client enabled compaction of cache files.</summary>
    public bool UseCompactor { get; set; }
    /// <summary>Maximum local cache size in GiB as configured in the source client.</summary>
    public float MaxLocalCacheInGiB { get; set; }
}

/// <summary>
/// Root object of server.json containing the list of servers and the selected index.
/// </summary>
public sealed class MareServerModelRoot
{
    /// <summary>Index of the currently selected server in <see cref="ServerStorage"/>.</summary>
    public int CurrentServer { get; set; }
    /// <summary>Collection of configured servers from the source plugin.</summary>
    public List<MareServerEntry> ServerStorage { get; set; } = new();
}

/// <summary>
/// Entry describing a single server as found in the source plugin's server.json.
/// </summary>
public sealed class MareServerEntry
{
    /// <summary>Human friendly name of the server.</summary>
    public string ServerName { get; set; } = "";
    /// <summary>API base URL of the server (http/https).</summary>
    public string ServerUri { get; set; } = "";
    /// <summary>Whether OAuth2 authentication is enabled for the server.</summary>
    public bool UseOAuth2 { get; set; }
    /// <summary>Optional OAuth access token when exported by the source plugin.</summary>
    public string? OAuthToken { get; set; }
    /// <summary>Secret keys stored by index; indices may be sparse.</summary>
    public Dictionary<int, MareSecretKey> SecretKeys { get; set; } = new();
    /// <summary>List of character authentications bound to this server.</summary>
    public List<MareAuth> Authentications { get; set; } = new();
}

/// <summary>
/// Represents a single secret key entry.
/// </summary>
public sealed class MareSecretKey
{
    /// <summary>Friendly label for the key.</summary>
    public string FriendlyName { get; set; } = "";
    /// <summary>Actual secret key value.</summary>
    public string Key { get; set; } = "";
}

/// <summary>
/// Represents a single character authentication binding.
/// </summary>
public sealed class MareAuth
{
    /// <summary>Character name as stored by the source plugin.</summary>
    public string CharacterName { get; set; } = "";
    /// <summary>World ID associated with the character.</summary>
    public uint WorldId { get; set; }
    /// <summary>Index of the secret key chosen for this character (-1 when none).</summary>
    public int SecretKeyIdx { get; set; }
    /// <summary>Optional UID assigned by the server.</summary>
    public string? UID { get; set; }
    /// <summary>Whether auto-login was enabled for this character.</summary>
    public bool AutoLogin { get; set; }
}
