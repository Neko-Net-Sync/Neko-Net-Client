// Neko-Net Client – File Cache Entity
// ----------------------------------------------------------------------------
// Represents a single indexed file in the local catalog. The same content hash
// can map to multiple physical files (Penumbra and cache variants). Paths are
// stored with logical prefixes ({penumbra}/{cache}) for portability and later
// resolved to absolute paths at runtime.
// ----------------------------------------------------------------------------
#nullable disable

namespace NekoNetClient.FileCache;

/// <summary>
/// Lightweight catalog record describing a file, including its content hash, logical path, and timestamps.
/// The <see cref="PrefixedFilePath"/> uses a logical prefix ({penumbra} or {cache}) to decouple the catalog
/// from machine-specific absolute paths. <see cref="ResolvedFilepath"/> contains the materialized absolute path.
/// </summary>
public class FileCacheEntity
{
    /// <summary>
    /// Constructs a new entity. Size fields may be null when not yet computed.
    /// </summary>
    public FileCacheEntity(string hash, string path, string lastModifiedDateTicks, long? size = null, long? compressedSize = null)
    {
        Size = size;
        CompressedSize = compressedSize;
        Hash = hash;
        PrefixedFilePath = path;
        LastModifiedDateTicks = lastModifiedDateTicks;
    }

    /// <summary>
    /// Approximate compressed size on disk, when known.
    /// </summary>
    public long? CompressedSize { get; set; }
    /// <summary>
    /// CSV representation: Hash|PrefixedPath|LastWriteTicks|Size|CompressedSize
    /// </summary>
    public string CsvEntry => $"{Hash}{FileCacheManager.CsvSplit}{PrefixedFilePath}{FileCacheManager.CsvSplit}{LastModifiedDateTicks}|{Size ?? -1}|{CompressedSize ?? -1}";
    /// <summary>
    /// 40-character SHA-1 hex string representing file content identity.
    /// </summary>
    public string Hash { get; set; }
    /// <summary>
    /// True when the file resides in the client cache directory (prefixed with {cache}).
    /// </summary>
    public bool IsCacheEntry => PrefixedFilePath.StartsWith(FileCacheManager.CachePrefix, StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// Last write time ticks in UTC captured when the entry was created or updated.
    /// </summary>
    public string LastModifiedDateTicks { get; set; }
    /// <summary>
    /// Logical path composed of a prefix and the relative location (e.g., {penumbra}\mod\... or {cache}\...).
    /// </summary>
    public string PrefixedFilePath { get; init; }
    /// <summary>
    /// Absolute path obtained by expanding the logical prefix against live configuration.
    /// </summary>
    public string ResolvedFilepath { get; private set; } = string.Empty;
    /// <summary>
    /// Uncompressed byte size, when known.
    /// </summary>
    public long? Size { get; set; }

    /// <summary>
    /// Sets the resolved absolute path, applying normalization to lower case and canonical slashes.
    /// </summary>
    public void SetResolvedFilePath(string filePath)
    {
        ResolvedFilepath = filePath.ToLowerInvariant().Replace("\\\\", "\\", StringComparison.Ordinal);
    }
}