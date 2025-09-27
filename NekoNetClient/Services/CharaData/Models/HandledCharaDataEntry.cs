//
// Neko-Net Client — HandledCharaDataEntry
// Purpose: Tracks a character that had CharaData applied locally, including whether it is the
//          local player, the Customize+ application id (if any), and the current meta info.
//
namespace NekoNetClient.Services.CharaData.Models;

/// <summary>
/// Describes a character for which CharaData has been applied and that should be reverted later.
/// </summary>
public sealed record HandledCharaDataEntry(string Name, bool IsSelf, Guid? CustomizePlus, CharaDataMetaInfoExtendedDto MetaInfo)
{
    /// <summary>
    /// Latest server meta information for the handled CharaData entry; updated in-place when
    /// refreshed metadata arrives.
    /// </summary>
    public CharaDataMetaInfoExtendedDto MetaInfo { get; set; } = MetaInfo;
}
