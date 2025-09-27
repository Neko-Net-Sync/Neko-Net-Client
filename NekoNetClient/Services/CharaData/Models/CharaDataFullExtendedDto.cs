//
// Neko-Net Client — CharaDataFullExtendedDto
// Purpose: Client extension around CharaDataFullDto providing a combined identifier and a
//          precomputed set of missing files to surface in UI.
//
using NekoNet.API.Dto.CharaData;
using System.Collections.ObjectModel;

namespace NekoNetClient.Services.CharaData.Models;

/// <summary>
/// Extended view model for a complete CharaData entry that precomputes the missing files list and
/// exposes a combined id for lookups.
/// </summary>
public sealed record CharaDataFullExtendedDto : CharaDataFullDto
{
    public CharaDataFullExtendedDto(CharaDataFullDto baseDto) : base(baseDto)
    {
        FullId = baseDto.Uploader.UID + ":" + baseDto.Id;
        MissingFiles = new ReadOnlyCollection<GamePathEntry>(baseDto.OriginalFiles.Except(baseDto.FileGamePaths).ToList());
        HasMissingFiles = MissingFiles.Any();
    }

    /// <summary>Combined uploader UID and id for convenient dictionary keys.</summary>
    public string FullId { get; set; }
    /// <summary>True when at least one original file is not present in the advertised files.</summary>
    public bool HasMissingFiles { get; init; }
    /// <summary>Read-only view of missing files for UI presentation.</summary>
    public IReadOnlyCollection<GamePathEntry> MissingFiles { get; init; }
}
