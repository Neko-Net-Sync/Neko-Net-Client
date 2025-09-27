// Neko-Net Client – File State
// ----------------------------------------------------------------------------
// Classification result when validating a catalog entry against current
// filesystem state. Guides subsequent actions in the cache manager and scanner.
// ----------------------------------------------------------------------------
namespace NekoNetClient.FileCache;

/// <summary>
/// Describes the validation status of a file catalog entry.
/// </summary>
public enum FileState
{
    /// <summary>
    /// File exists and timestamp is unchanged from catalog; entry considered healthy.
    /// </summary>
    Valid,
    /// <summary>
    /// File exists but timestamp changed; recompute size/hash and update the catalog entry.
    /// </summary>
    RequireUpdate,
    /// <summary>
    /// File is missing; remove entry from catalog.
    /// </summary>
    RequireDeletion,
}