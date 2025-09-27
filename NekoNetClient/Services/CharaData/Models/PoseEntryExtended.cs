//
// Neko-Net Client — PoseEntryExtended
// Purpose: UI-friendly extension of PoseEntry that precomputes map coordinates, world descriptors,
//          and vector/quaternion structures for consumption by overlays or inspectors.
//
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using NekoNet.API.Dto.CharaData;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace NekoNetClient.Services.CharaData.Models;

/// <summary>
/// Extends a <see cref="PoseEntry"/> with derived information like map coordinates and nicely
/// formatted world data strings. Uses <see cref="DalamudUtilService"/> to resolve mapping info.
/// </summary>
public sealed record PoseEntryExtended : PoseEntry
{
    private PoseEntryExtended(PoseEntry basePose, CharaDataMetaInfoExtendedDto parent) : base(basePose)
    {
        HasPoseData = !string.IsNullOrEmpty(basePose.PoseData);
        HasWorldData = (WorldData ?? default) != default;
        if (HasWorldData)
        {
            Position = new(basePose.WorldData!.Value.PositionX, basePose.WorldData!.Value.PositionY, basePose.WorldData!.Value.PositionZ);
            Rotation = new(basePose.WorldData!.Value.RotationX, basePose.WorldData!.Value.RotationY, basePose.WorldData!.Value.RotationZ, basePose.WorldData!.Value.RotationW);
        }
        MetaInfo = parent;
    }

    /// <summary>Back-reference to the extended meta info that owns this pose entry.</summary>
    public CharaDataMetaInfoExtendedDto MetaInfo { get; }
    /// <summary>True if the pose has non-empty pose data.</summary>
    public bool HasPoseData { get; }
    /// <summary>True if the pose contains world data.</summary>
    public bool HasWorldData { get; }
    /// <summary>World position vector derived from world data.</summary>
    public Vector3 Position { get; } = new();
    /// <summary>Map coordinates resolved via Dalamud.</summary>
    public Vector2 MapCoordinates { get; private set; } = new();
    /// <summary>World rotation quaternion derived from world data.</summary>
    public Quaternion Rotation { get; } = new();
    /// <summary>Resolved Lumina map reference when world data is present.</summary>
    public Map Map { get; private set; }
    /// <summary>Human-readable description of the world data (server, map, ward, coords).</summary>
    public string WorldDataDescriptor { get; private set; } = string.Empty;

    /// <summary>
    /// Factory that builds an extended pose from a base entry and resolves mapping/descriptor
    /// fields asynchronously using the provided Dalamud utilities.
    /// </summary>
    public static async Task<PoseEntryExtended> Create(PoseEntry baseEntry, CharaDataMetaInfoExtendedDto parent, DalamudUtilService dalamudUtilService)
    {
        PoseEntryExtended newPose = new(baseEntry, parent);

        if (newPose.HasWorldData)
        {
            var worldData = newPose.WorldData!.Value;
            newPose.MapCoordinates = await dalamudUtilService.RunOnFrameworkThread(() =>
                    MapUtil.WorldToMap(new Vector2(worldData.PositionX, worldData.PositionY), dalamudUtilService.MapData.Value[worldData.LocationInfo.MapId].Map))
                .ConfigureAwait(false);
            newPose.Map = dalamudUtilService.MapData.Value[worldData.LocationInfo.MapId].Map;

            StringBuilder sb = new();
            sb.AppendLine("Server: " + dalamudUtilService.WorldData.Value[(ushort)worldData.LocationInfo.ServerId]);
            sb.AppendLine("Territory: " + dalamudUtilService.TerritoryData.Value[worldData.LocationInfo.TerritoryId]);
            sb.AppendLine("Map: " + dalamudUtilService.MapData.Value[worldData.LocationInfo.MapId].MapName);

            if (worldData.LocationInfo.WardId != 0)
                sb.AppendLine("Ward #: " + worldData.LocationInfo.WardId);
            if (worldData.LocationInfo.DivisionId != 0)
            {
                sb.AppendLine("Subdivision: " + worldData.LocationInfo.DivisionId switch
                {
                    1 => "No",
                    2 => "Yes",
                    _ => "-"
                });
            }
            if (worldData.LocationInfo.HouseId != 0)
            {
                sb.AppendLine("House #: " + (worldData.LocationInfo.HouseId == 100 ? "Apartments" : worldData.LocationInfo.HouseId.ToString()));
            }
            if (worldData.LocationInfo.RoomId != 0)
            {
                sb.AppendLine("Apartment #: " + worldData.LocationInfo.RoomId);
            }
            sb.AppendLine("Coordinates: X: " + newPose.MapCoordinates.X.ToString("0.0", CultureInfo.InvariantCulture) + ", Y: " + newPose.MapCoordinates.Y.ToString("0.0", CultureInfo.InvariantCulture));
            newPose.WorldDataDescriptor = sb.ToString();
        }

        return newPose;
    }
}