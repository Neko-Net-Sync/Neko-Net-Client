/*
   Neko-Net Client — PlayerData.Data.CharacterDataFragment
   -------------------------------------------------------
   Purpose
   - Snapshot for a single object kind containing Customize+, file replacements, and Glamourer data.
*/
namespace NekoNetClient.PlayerData.Data;

/// <summary>
/// Snapshot of per-kind character data such as Customize+ scale, file replacements and Glamourer string.
/// </summary>
public class CharacterDataFragment
{
    /// <summary>The Customize+ scale serialized value.</summary>
    public string CustomizePlusScale { get; set; } = string.Empty;
    /// <summary>Resolved file replacements for this object kind.</summary>
    public HashSet<FileReplacement> FileReplacements { get; set; } = [];
    /// <summary>Glamourer data (appearance/customization) serialized string.</summary>
    public string GlamourerString { get; set; } = string.Empty;
}
