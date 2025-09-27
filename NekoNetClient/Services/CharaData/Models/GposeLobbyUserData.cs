//
// Neko-Net Client — GposeLobbyUserData
// Purpose: Tracks per-user GPose session state, including world and pose deltas, target
//          transitions for movement, and last applied CharaData timestamps to throttle updates.
//
using Dalamud.Utility;
using NekoNet.API.Data;
using NekoNet.API.Dto.CharaData;
using NekoNetClient.Utils;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace NekoNetClient.Services.CharaData.Models;

/// <summary>
/// Encapsulates user-centric state used in GPose lobbies for visualizing positions, applying
/// pose deltas, and coordinating effects. Provides helper logic to combine full and delta pose
/// information into an applicable form for rendering.
/// </summary>
public sealed record GposeLobbyUserData(UserData UserData)
{
    /// <summary>Resets transient update flags and timestamps.</summary>
    public void Reset()
    {
        HasWorldDataUpdate = WorldData != null;
        HasPoseDataUpdate = ApplicablePoseData != null;
        SpawnedVfxId = null;
        LastAppliedCharaDataDate = DateTime.MinValue;
    }

    private WorldData? _worldData;
    /// <summary>Latest world data; setting toggles the update flag.</summary>
    public WorldData? WorldData
    {
        get => _worldData; set
        {
            _worldData = value;
            HasWorldDataUpdate = true;
        }
    }

    /// <summary>True when new world data is available and should be consumed.</summary>
    public bool HasWorldDataUpdate { get; set; } = false;

    private PoseData? _fullPoseData;
    private PoseData? _deltaPoseData;

    /// <summary>Latest full pose data; setting re-computes applicable pose and flags an update.</summary>
    public PoseData? FullPoseData
    {
        get => _fullPoseData;
        set
        {
            _fullPoseData = value;
            ApplicablePoseData = CombinePoseData();
            HasPoseDataUpdate = true;
        }
    }

    /// <summary>Latest delta pose; setting re-computes applicable pose and flags an update.</summary>
    public PoseData? DeltaPoseData
    {
        get => _deltaPoseData;
        set
        {
            _deltaPoseData = value;
            ApplicablePoseData = CombinePoseData();
            HasPoseDataUpdate = true;
        }
    }

    /// <summary>Computed pose taking deltas into account.</summary>
    public PoseData? ApplicablePoseData { get; private set; }
    /// <summary>True if a new applicable pose is available.</summary>
    public bool HasPoseDataUpdate { get; set; } = false;
    /// <summary>Optional VFX id spawned for this user.</summary>
    public Guid? SpawnedVfxId { get; set; }
    /// <summary>Last known world position.</summary>
    public Vector3? LastWorldPosition { get; set; }
    /// <summary>Target world position for smooth transitions.</summary>
    public Vector3? TargetWorldPosition { get; set; }
    /// <summary>Time when the current transition started.</summary>
    public DateTime? UpdateStart { get; set; }
    private CharaDataDownloadDto? _charaData;
    /// <summary>Latest chara data; sets the <see cref="LastUpdatedCharaData"/> timestamp.</summary>
    public CharaDataDownloadDto? CharaData
    {
        get => _charaData; set
        {
            _charaData = value;
            LastUpdatedCharaData = _charaData?.UpdatedDate ?? DateTime.MaxValue;
        }
    }

    /// <summary>Timestamp of the server-provided chara data's last update.</summary>
    public DateTime LastUpdatedCharaData { get; private set; } = DateTime.MaxValue;
    /// <summary>When the local client last applied this user's chara data.</summary>
    public DateTime LastAppliedCharaDataDate { get; set; } = DateTime.MinValue;
    /// <summary>Dalamud object address for the associated actor.</summary>
    public nint Address { get; set; }
    /// <summary>Associated character name when address resolution isn't available yet.</summary>
    public string AssociatedCharaName { get; set; } = string.Empty;

    private PoseData? CombinePoseData()
    {
        if (DeltaPoseData == null && FullPoseData != null) return FullPoseData;
        if (FullPoseData == null) return null;

        PoseData output = FullPoseData!.Value.DeepClone();
        PoseData delta = DeltaPoseData!.Value;

        foreach (var bone in FullPoseData!.Value.Bones)
        {
            if (!delta.Bones.TryGetValue(bone.Key, out var data)) continue;
            if (!data.Exists)
            {
                output.Bones.Remove(bone.Key);
            }
            else
            {
                output.Bones[bone.Key] = data;
            }
        }

        foreach (var bone in FullPoseData!.Value.MainHand)
        {
            if (!delta.MainHand.TryGetValue(bone.Key, out var data)) continue;
            if (!data.Exists)
            {
                output.MainHand.Remove(bone.Key);
            }
            else
            {
                output.MainHand[bone.Key] = data;
            }
        }

        foreach (var bone in FullPoseData!.Value.OffHand)
        {
            if (!delta.OffHand.TryGetValue(bone.Key, out var data)) continue;
            if (!data.Exists)
            {
                output.OffHand.Remove(bone.Key);
            }
            else
            {
                output.OffHand[bone.Key] = data;
            }
        }

        return output;
    }

    /// <summary>Human-readable description of the last world data.</summary>
    public string WorldDataDescriptor { get; private set; } = string.Empty;
    /// <summary>Computed map coordinates corresponding to <see cref="WorldData"/>.</summary>
    public Vector2 MapCoordinates { get; private set; }
    /// <summary>The Lumina map resolved for the world position.</summary>
    public Lumina.Excel.Sheets.Map Map { get; private set; }
    /// <summary>Reference to a handled chara entry when this user is currently applied locally.</summary>
    public HandledCharaDataEntry? HandledChara { get; set; }

    /// <summary>
    /// Computes a descriptive summary for the current world data and resolves map coordinates.
    /// </summary>
    public async Task SetWorldDataDescriptor(DalamudUtilService dalamudUtilService)
    {
        if (WorldData == null)
        {
            WorldDataDescriptor = "No World Data found";
        }

        var worldData = WorldData!.Value;
        MapCoordinates = await dalamudUtilService.RunOnFrameworkThread(() =>
                MapUtil.WorldToMap(new Vector2(worldData.PositionX, worldData.PositionY), dalamudUtilService.MapData.Value[worldData.LocationInfo.MapId].Map))
            .ConfigureAwait(false);
        Map = dalamudUtilService.MapData.Value[worldData.LocationInfo.MapId].Map;

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
        sb.AppendLine("Coordinates: X: " + MapCoordinates.X.ToString("0.0", CultureInfo.InvariantCulture) + ", Y: " + MapCoordinates.Y.ToString("0.0", CultureInfo.InvariantCulture));
        WorldDataDescriptor = sb.ToString();
    }
}
