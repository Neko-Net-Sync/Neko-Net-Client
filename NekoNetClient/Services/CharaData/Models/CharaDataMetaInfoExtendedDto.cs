//
// Neko-Net Client — CharaDataMetaInfoExtendedDto
// Purpose: Client-side extension of server meta info with convenience properties like FullId and
//          expanded Pose entries pre-resolved to map and world descriptors.
//
using NekoNet.API.Dto.CharaData;

namespace NekoNetClient.Services.CharaData.Models;

/// <summary>
/// Extended meta info for CharaData entries adding convenience and UI-centric properties such as
/// a combined identifier (<see cref="FullId"/>), a compiled list of <see cref="PoseEntryExtended"/>,
/// and flags for pose/world data availability.
/// </summary>
public sealed record CharaDataMetaInfoExtendedDto : CharaDataMetaInfoDto
{
    private CharaDataMetaInfoExtendedDto(CharaDataMetaInfoDto baseMeta) : base(baseMeta)
    {
        FullId = baseMeta.Uploader.UID + ":" + baseMeta.Id;
    }

    /// <summary>Expanded pose information enriched with world/map metadata.</summary>
    public List<PoseEntryExtended> PoseExtended { get; private set; } = [];
    /// <summary>True when any pose information is present.</summary>
    public bool HasPoses => PoseExtended.Count != 0;
    /// <summary>True when any pose includes world data (location).</summary>
    public bool HasWorldData => PoseExtended.Exists(p => p.HasWorldData);
    /// <summary>True when this data was uploaded by the local user.</summary>
    public bool IsOwnData { get; private set; }
    /// <summary>Combined unique id composed of uploader UID and server id.</summary>
    public string FullId { get; private set; }

    /// <summary>
    /// Creates a new extended meta info entry and asynchronously resolves additional pose
    /// information on the framework thread when necessary.
    /// </summary>
    public async static Task<CharaDataMetaInfoExtendedDto> Create(CharaDataMetaInfoDto baseMeta, DalamudUtilService dalamudUtilService, bool isOwnData = false)
    {
        CharaDataMetaInfoExtendedDto newDto = new(baseMeta);

        foreach (var pose in newDto.PoseData)
        {
            newDto.PoseExtended.Add(await PoseEntryExtended.Create(pose, newDto, dalamudUtilService).ConfigureAwait(false));
        }

        newDto.IsOwnData = isOwnData;

        return newDto;
    }
}
