/*
   Neko-Net Client — PlayerData.Data.CharacterDataFragmentPlayer
   -------------------------------------------------------------
   Purpose
   - Extends CharacterDataFragment with player-only data from optional plugins and manipulations.
*/
namespace NekoNetClient.PlayerData.Data;

/// <summary>
/// Extension of <see cref="CharacterDataFragment"/> for players that includes Heels, Honorific, manipulations,
/// Moodles, and PetNames data.
/// </summary>
public class CharacterDataFragmentPlayer : CharacterDataFragment
{
    /// <summary>Heels serialized offset data.</summary>
    public string HeelsData { get; set; } = string.Empty;
    /// <summary>Honorific title string.</summary>
    public string HonorificData { get; set; } = string.Empty;
    /// <summary>Penumbra manipulations string.</summary>
    public string ManipulationString { get; set; } = string.Empty;
    /// <summary>Moodles status string.</summary>
    public string MoodlesData { get; set; } = string.Empty;
    /// <summary>Pet names serialized data.</summary>
    public string PetNamesData { get; set; } = string.Empty;
}
