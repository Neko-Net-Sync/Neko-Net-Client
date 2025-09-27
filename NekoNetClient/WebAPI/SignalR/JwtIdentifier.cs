/*
   File: JwtIdentifier.cs
   Role: Composite key describing the context for a cached JWT, including API URL, character hash, UID, and secret/OAuth.
*/
namespace NekoNetClient.WebAPI.SignalR;

/// <summary>
/// Identifies a unique JWT context (server URL + character + auth token seed) for caching and renewal.
/// </summary>
public record JwtIdentifier(string ApiUrl, string CharaHash, string UID, string SecretKeyOrOAuth)
{
    public override string ToString()
    {
        return "{JwtIdentifier; Url: " + ApiUrl + ", Chara: " + CharaHash + ", UID: " + UID + ", HasSecretKeyOrOAuth: " + !string.IsNullOrEmpty(SecretKeyOrOAuth) + "}";
    }
}