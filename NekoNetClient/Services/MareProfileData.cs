/*
    Neko-Net Client — Services.MareProfileData
    ----------------------------------------
    Purpose
    - Data container and helper methods for profile details shown in UI panels.
*/
namespace NekoNetClient.Services;

/// <summary>
/// Immutable profile data as shown in profile-related UI. Contains flags, text, and base64-encoded images,
/// alongside lazy byte arrays for convenient decoding on demand.
/// </summary>
public record MareProfileData(bool IsFlagged, bool IsNSFW, string Base64ProfilePicture, string Base64SupporterPicture, string Description)
{
    /// <summary>
    /// Decoded profile picture bytes, evaluated upon first access from <see cref="Base64ProfilePicture"/>.
    /// </summary>
    public Lazy<byte[]> ImageData { get; } = new Lazy<byte[]>(Convert.FromBase64String(Base64ProfilePicture));
    /// <summary>
    /// Decoded supporter picture bytes, or an empty array if no supporter image is set.
    /// </summary>
    public Lazy<byte[]> SupporterImageData { get; } = new Lazy<byte[]>(string.IsNullOrEmpty(Base64SupporterPicture) ? [] : Convert.FromBase64String(Base64SupporterPicture));
}
