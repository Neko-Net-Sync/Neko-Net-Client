/*
   Neko-Net Client — PlayerData.Pairs.OptionalPluginWarning
   --------------------------------------------------------
   Purpose
   - Lightweight record tracking whether optional plugin warnings were already displayed for a user.
*/
namespace NekoNetClient.PlayerData.Pairs;

/// <summary>
/// Tracks which optional plugin warnings (e.g., Heels, Customize+, Honorific, Moodles, Pet Names)
/// have already been shown to the user so they aren't repeatedly displayed.
/// </summary>
public record OptionalPluginWarning
{
    public bool ShownHeelsWarning { get; set; } = false;
    public bool ShownCustomizePlusWarning { get; set; } = false;
    public bool ShownHonorificWarning { get; set; } = false;
    public bool ShownMoodlesWarning { get; set; } = false;
    public bool ShowPetNicknamesWarning { get; set; } = false;
}