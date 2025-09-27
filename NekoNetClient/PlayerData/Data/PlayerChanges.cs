/*
   Neko-Net Client — PlayerData.Data.PlayerChanges
   -----------------------------------------------
   Purpose
   - Enumerates the kinds of player-related changes that can be applied by the PairHandler pipeline.
*/
namespace NekoNetClient.PlayerData.Data;

/// <summary>
/// Types of per-kind changes used to drive the apply pipeline order and behavior.
/// </summary>
public enum PlayerChanges
{
    ModFiles = 1,
    ModManip = 2,
    Glamourer = 3,
    Customize = 4,
    Heels = 5,
    Honorific = 7,
    ForcedRedraw = 8,
    Moodles = 9,
    PetNames = 10,
}