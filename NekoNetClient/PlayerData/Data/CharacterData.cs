/*
   Neko-Net Client — PlayerData.Data.CharacterData
   -----------------------------------------------
   Purpose
   - Aggregates per-object-kind character customization and file replacement data, and converts it to the
     API transport model for upload.
*/
using NekoNet.API.Data;
using NekoNet.API.Data.Enum;

namespace NekoNetClient.PlayerData.Data;

/// <summary>
/// Aggregate of character customization and file replacement data grouped by <see cref="ObjectKind"/>.
/// Provides conversion to the API transport model and helpers to merge per-kind fragments.
/// </summary>
public class CharacterData
{
    /// <summary>Customize+ scale per object kind.</summary>
    public Dictionary<ObjectKind, string> CustomizePlusScale { get; set; } = [];
    /// <summary>Resolved file replacements per object kind.</summary>
    public Dictionary<ObjectKind, HashSet<FileReplacement>> FileReplacements { get; set; } = [];
    /// <summary>Glamourer configuration per object kind.</summary>
    public Dictionary<ObjectKind, string> GlamourerString { get; set; } = [];
    /// <summary>Heels plugin data for players.</summary>
    public string HeelsData { get; set; } = string.Empty;
    /// <summary>Honorific plugin data for players.</summary>
    public string HonorificData { get; set; } = string.Empty;
    /// <summary>Penumbra manipulations serialized string for players.</summary>
    public string ManipulationString { get; set; } = string.Empty;
    /// <summary>Moodles plugin data for players.</summary>
    public string MoodlesData { get; set; } = string.Empty;
    /// <summary>PetNames plugin data for players.</summary>
    public string PetNamesData { get; set; } = string.Empty;

    /// <summary>
    /// Applies a fragment for an <paramref name="kind"/>. Null fragments clear the per-kind data.
    /// </summary>
    public void SetFragment(ObjectKind kind, CharacterDataFragment? fragment)
    {
        if (kind == ObjectKind.Player)
        {
            var playerFragment = (fragment as CharacterDataFragmentPlayer);
            HeelsData = playerFragment?.HeelsData ?? string.Empty;
            HonorificData = playerFragment?.HonorificData ?? string.Empty;
            ManipulationString = playerFragment?.ManipulationString ?? string.Empty;
            MoodlesData = playerFragment?.MoodlesData ?? string.Empty;
            PetNamesData = playerFragment?.PetNamesData ?? string.Empty;
        }

        if (fragment is null)
        {
            CustomizePlusScale.Remove(kind);
            FileReplacements.Remove(kind);
            GlamourerString.Remove(kind);
        }
        else
        {
            CustomizePlusScale[kind] = fragment.CustomizePlusScale;
            FileReplacements[kind] = fragment.FileReplacements;
            GlamourerString[kind] = fragment.GlamourerString;
        }
    }

    /// <summary>
    /// Converts this aggregate into the API transport type, grouping non-swap replacements by hash
    /// and appending file swaps as separate entries.
    /// </summary>
    public NekoNet.API.Data.CharacterData ToAPI()
    {
        Dictionary<ObjectKind, List<FileReplacementData>> fileReplacements =
            FileReplacements.ToDictionary(k => k.Key, k => k.Value.Where(f => f.HasFileReplacement && !f.IsFileSwap)
            .GroupBy(f => f.Hash, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
        {
            return new FileReplacementData()
            {
                GamePaths = g.SelectMany(f => f.GamePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Hash = g.First().Hash,
            };
        }).ToList());

        foreach (var item in FileReplacements)
        {
            var fileSwapsToAdd = item.Value.Where(f => f.IsFileSwap).Select(f => f.ToFileReplacementDto());
            fileReplacements[item.Key].AddRange(fileSwapsToAdd);
        }

        return new NekoNet.API.Data.CharacterData()
        {
            FileReplacements = fileReplacements,
            GlamourerData = GlamourerString.ToDictionary(d => d.Key, d => d.Value),
            ManipulationData = ManipulationString,
            HeelsData = HeelsData,
            CustomizePlusData = CustomizePlusScale.ToDictionary(d => d.Key, d => d.Value),
            HonorificData = HonorificData,
            MoodlesData = MoodlesData,
            PetNamesData = PetNamesData
        };
    }
}