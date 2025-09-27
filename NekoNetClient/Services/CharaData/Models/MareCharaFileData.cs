//
// Neko-Net Client — MareCharaFileData
// Purpose: JSON-serialized metadata embedded in MCDF files, capturing appearance data and the
//          list of files/swaps needed to reproduce the look.
//
using NekoNet.API.Data;
using NekoNet.API.Data.Enum;
using NekoNetClient.FileCache;
using System.Text;
using System.Text.Json;

namespace NekoNetClient.Services.CharaData.Models;

/// <summary>
/// Serializable data model describing a character's appearance and required files. Embedded as a
/// JSON payload within an MCDF container and used to reconstruct appearances elsewhere.
/// </summary>
public record MareCharaFileData
{
    /// <summary>Free-form description for the MCDF.</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>Glamourer state for the player object.</summary>
    public string GlamourerData { get; set; } = string.Empty;
    /// <summary>Customize+ state for the player object, if present.</summary>
    public string CustomizePlusData { get; set; } = string.Empty;
    /// <summary>Collection-level manipulation data (Penumbra collections etc.).</summary>
    public string ManipulationData { get; set; } = string.Empty;
    /// <summary>List of files required by hash and their associated game paths.</summary>
    public List<FileData> Files { get; set; } = [];
    /// <summary>List of file swaps to apply directly.</summary>
    public List<FileSwap> FileSwaps { get; set; } = [];

    public MareCharaFileData() { }
    /// <summary>
    /// Constructs an MCDF payload from a captured <see cref="CharacterData"/> by grouping files
    /// and resolving sizes via the cache manager. Filters are applied by the caller beforehand.
    /// </summary>
    public MareCharaFileData(FileCacheManager manager, string description, CharacterData dto)
    {
        Description = description;

        if (dto.GlamourerData.TryGetValue(ObjectKind.Player, out var glamourerData))
        {
            GlamourerData = glamourerData;
        }

        dto.CustomizePlusData.TryGetValue(ObjectKind.Player, out var customizePlusData);
        CustomizePlusData = customizePlusData ?? string.Empty;
        ManipulationData = dto.ManipulationData;

        if (dto.FileReplacements.TryGetValue(ObjectKind.Player, out var fileReplacements))
        {
            var grouped = fileReplacements.GroupBy(f => f.Hash, StringComparer.OrdinalIgnoreCase);

            foreach (var file in grouped)
            {
                if (string.IsNullOrEmpty(file.Key))
                {
                    foreach (var item in file)
                    {
                        FileSwaps.Add(new FileSwap(item.GamePaths, item.FileSwapPath));
                    }
                }
                else
                {
                    var filePath = manager.GetFileCacheByHash(file.First().Hash)?.ResolvedFilepath;
                    if (filePath != null)
                    {
                        Files.Add(new FileData(file.SelectMany(f => f.GamePaths), (int)new FileInfo(filePath).Length, file.First().Hash));
                    }
                }
            }
        }
    }

    /// <summary>Serializes this instance to UTF-8 JSON bytes.</summary>
    public byte[] ToByteArray()
    {
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
    }

    /// <summary>Deserializes an instance from UTF-8 JSON bytes.</summary>
    public static MareCharaFileData FromByteArray(byte[] data)
    {
        return JsonSerializer.Deserialize<MareCharaFileData>(Encoding.UTF8.GetString(data))!;
    }

    /// <summary>Represents a file swap entry mapping game paths directly to an alternative path.</summary>
    public record FileSwap(IEnumerable<string> GamePaths, string FileSwapPath);

    /// <summary>Represents a file entry with its game paths, size in bytes, and hash.</summary>
    public record FileData(IEnumerable<string> GamePaths, int Length, string Hash);
}