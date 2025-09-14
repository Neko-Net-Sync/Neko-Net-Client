namespace NekoNetClient.NekoHelpers.ImportModels;

// mirrors config.json
public sealed class MareConfigModel
{
    public bool AcceptedAgreement { get; set; }
    public string? CacheFolder { get; set; }
    public bool InitialScanComplete { get; set; }
    public bool UseCompactor { get; set; }
    public float MaxLocalCacheInGiB { get; set; }
}

// mirrors server.json
public sealed class MareServerModelRoot
{
    public int CurrentServer { get; set; }
    public List<MareServerEntry> ServerStorage { get; set; } = new();
}

public sealed class MareServerEntry
{
    public string ServerName { get; set; } = "";
    public string ServerUri { get; set; } = "";
    public bool UseOAuth2 { get; set; }
    public string? OAuthToken { get; set; }
    public Dictionary<int, MareSecretKey> SecretKeys { get; set; } = new();
    public List<MareAuth> Authentications { get; set; } = new();
}

public sealed class MareSecretKey
{
    public string FriendlyName { get; set; } = "";
    public string Key { get; set; } = "";
}

public sealed class MareAuth
{
    public string CharacterName { get; set; } = "";
    public uint WorldId { get; set; }
    public int SecretKeyIdx { get; set; }
    public string? UID { get; set; }
    public bool AutoLogin { get; set; }
}
