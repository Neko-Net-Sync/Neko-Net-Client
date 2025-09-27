//
// Neko-Net Client — CharaDataExtendedUpdateDto
// Purpose: Wraps CharaDataUpdateDto and provides diff-aware setters that null out unchanged
//          properties to minimize payloads. Also exposes mutable lists for easy UI editing.
//
using NekoNet.API.Data;
using NekoNet.API.Dto.CharaData;

namespace NekoNetClient.Services.CharaData.Models;

/// <summary>
/// Diff-driven wrapper around <see cref="CharaDataUpdateDto"/> that tracks local edits against a
/// baseline <see cref="CharaDataFullDto"/> and clears properties back to null when unchanged, so
/// the server receives minimal updates.
/// </summary>
public sealed record CharaDataExtendedUpdateDto : CharaDataUpdateDto
{
    private readonly CharaDataFullDto _charaDataFullDto;

    /// <summary>
    /// Creates the wrapper and initializes working copies of allowed users, groups, and poses
    /// from the provided base data.
    /// </summary>
    public CharaDataExtendedUpdateDto(CharaDataUpdateDto dto, CharaDataFullDto charaDataFullDto) : base(dto)
    {
        _charaDataFullDto = charaDataFullDto;
        _userList = charaDataFullDto.AllowedUsers.ToList();
        _groupList = charaDataFullDto.AllowedGroups.ToList();
        _poseList = charaDataFullDto.PoseData.Select(k => new PoseEntry(k.Id)
        {
            Description = k.Description,
            PoseData = k.PoseData,
            WorldData = k.WorldData
        }).ToList();
    }

    /// <summary>Returns a clean update DTO containing only changed properties.</summary>
    public CharaDataUpdateDto BaseDto => new(Id)
    {
        AllowedUsers = AllowedUsers,
        AllowedGroups = AllowedGroups,
        AccessType = base.AccessType,
        CustomizeData = base.CustomizeData,
        Description = base.Description,
        ExpiryDate = base.ExpiryDate,
        FileGamePaths = base.FileGamePaths,
        FileSwaps = base.FileSwaps,
        GlamourerData = base.GlamourerData,
        ShareType = base.ShareType,
        ManipulationData = base.ManipulationData,
        Poses = Poses
    };

    /// <summary>
    /// Diff-aware ManipulationData. Returns baseline value when unchanged; sets null when equal
    /// to baseline to avoid redundant updates.
    /// </summary>
    public new string ManipulationData
    {
        get
        {
            return base.ManipulationData ?? _charaDataFullDto.ManipulationData;
        }
        set
        {
            base.ManipulationData = value;
            if (string.Equals(base.ManipulationData, _charaDataFullDto.ManipulationData, StringComparison.Ordinal))
            {
                base.ManipulationData = null;
            }
        }
    }

    /// <summary>
    /// Diff-aware Description.
    /// </summary>
    public new string Description
    {
        get
        {
            return base.Description ?? _charaDataFullDto.Description;
        }
        set
        {
            base.Description = value;
            if (string.Equals(base.Description, _charaDataFullDto.Description, StringComparison.Ordinal))
            {
                base.Description = null;
            }
        }
    }

    /// <summary>
    /// Diff-aware ExpiryDate; setter is private for controlled updates via helpers.
    /// </summary>
    public new DateTime ExpiryDate
    {
        get
        {
            return base.ExpiryDate ?? _charaDataFullDto.ExpiryDate;
        }
        private set
        {
            base.ExpiryDate = value;
            if (Equals(base.ExpiryDate, _charaDataFullDto.ExpiryDate))
            {
                base.ExpiryDate = null;
            }
        }
    }

    /// <summary>
    /// Diff-aware AccessType.
    /// </summary>
    public new AccessTypeDto AccessType
    {
        get
        {
            return base.AccessType ?? _charaDataFullDto.AccessType;
        }
        set
        {
            base.AccessType = value;

            if (Equals(base.AccessType, _charaDataFullDto.AccessType))
            {
                base.AccessType = null;
            }
        }
    }

    /// <summary>
    /// Diff-aware ShareType.
    /// </summary>
    public new ShareTypeDto ShareType
    {
        get
        {
            return base.ShareType ?? _charaDataFullDto.ShareType;
        }
        set
        {
            base.ShareType = value;

            if (Equals(base.ShareType, _charaDataFullDto.ShareType))
            {
                base.ShareType = null;
            }
        }
    }

    /// <summary>
    /// Diff-aware FileGamePaths.
    /// </summary>
    public new List<GamePathEntry>? FileGamePaths
    {
        get
        {
            return base.FileGamePaths ?? _charaDataFullDto.FileGamePaths;
        }
        set
        {
            base.FileGamePaths = value;
            if (!(base.FileGamePaths ?? []).Except(_charaDataFullDto.FileGamePaths).Any()
                && !_charaDataFullDto.FileGamePaths.Except(base.FileGamePaths ?? []).Any())
            {
                base.FileGamePaths = null;
            }
        }
    }

    /// <summary>
    /// Diff-aware FileSwaps.
    /// </summary>
    public new List<GamePathEntry>? FileSwaps
    {
        get
        {
            return base.FileSwaps ?? _charaDataFullDto.FileSwaps;
        }
        set
        {
            base.FileSwaps = value;
            if (!(base.FileSwaps ?? []).Except(_charaDataFullDto.FileSwaps).Any()
                && !_charaDataFullDto.FileSwaps.Except(base.FileSwaps ?? []).Any())
            {
                base.FileSwaps = null;
            }
        }
    }

    /// <summary>
    /// Diff-aware GlamourerData.
    /// </summary>
    public new string? GlamourerData
    {
        get
        {
            return base.GlamourerData ?? _charaDataFullDto.GlamourerData;
        }
        set
        {
            base.GlamourerData = value;
            if (string.Equals(base.GlamourerData, _charaDataFullDto.GlamourerData, StringComparison.Ordinal))
            {
                base.GlamourerData = null;
            }
        }
    }

    /// <summary>
    /// Diff-aware CustomizeData.
    /// </summary>
    public new string? CustomizeData
    {
        get
        {
            return base.CustomizeData ?? _charaDataFullDto.CustomizeData;
        }
        set
        {
            base.CustomizeData = value;
            if (string.Equals(base.CustomizeData, _charaDataFullDto.CustomizeData, StringComparison.Ordinal))
            {
                base.CustomizeData = null;
            }
        }
    }

    /// <summary>Mutable working list for UI to manage allowed users.</summary>
    public IEnumerable<UserData> UserList => _userList;
    private readonly List<UserData> _userList;

    /// <summary>Mutable working list for UI to manage allowed groups.</summary>
    public IEnumerable<GroupData> GroupList => _groupList;
    private readonly List<GroupData> _groupList;

    /// <summary>Mutable working list for UI to manage poses.</summary>
    public IEnumerable<PoseEntry> PoseList => _poseList;
    private readonly List<PoseEntry> _poseList;

    /// <summary>Adds a user to the working list and updates the diff.</summary>
    public void AddUserToList(string user)
    {
        _userList.Add(new(user, null));
        UpdateAllowedUsers();
    }

    /// <summary>Adds a group to the working list and updates the diff.</summary>
    public void AddGroupToList(string group)
    {
        _groupList.Add(new(group, null));
        UpdateAllowedGroups();
    }

    private void UpdateAllowedUsers()
    {
        AllowedUsers = [.. _userList.Select(u => u.UID)];
        if (!AllowedUsers.Except(_charaDataFullDto.AllowedUsers.Select(u => u.UID), StringComparer.Ordinal).Any()
            && !_charaDataFullDto.AllowedUsers.Select(u => u.UID).Except(AllowedUsers, StringComparer.Ordinal).Any())
        {
            AllowedUsers = null;
        }
    }

    private void UpdateAllowedGroups()
    {
        AllowedGroups = [.. _groupList.Select(u => u.GID)];
        if (!AllowedGroups.Except(_charaDataFullDto.AllowedGroups.Select(u => u.GID), StringComparer.Ordinal).Any()
            && !_charaDataFullDto.AllowedGroups.Select(u => u.GID).Except(AllowedGroups, StringComparer.Ordinal).Any())
        {
            AllowedGroups = null;
        }
    }

    /// <summary>Removes a user and updates the diff.</summary>
    public void RemoveUserFromList(string user)
    {
        _userList.RemoveAll(u => string.Equals(u.UID, user, StringComparison.Ordinal));
        UpdateAllowedUsers();
    }

    /// <summary>Removes a group and updates the diff.</summary>
    public void RemoveGroupFromList(string group)
    {
        _groupList.RemoveAll(u => string.Equals(u.GID, group, StringComparison.Ordinal));
        UpdateAllowedGroups();
    }

    /// <summary>Adds a new empty pose to the working list and updates the diff.</summary>
    public void AddPose()
    {
        _poseList.Add(new PoseEntry(null));
        UpdatePoseList();
    }

    /// <summary>
    /// Removes a pose from the working list. When the pose existed previously, fields are nulled
    /// out to signal deletion to the server. For new poses, the entry is removed entirely.
    /// </summary>
    public void RemovePose(PoseEntry entry)
    {
        if (entry.Id != null)
        {
            entry.Description = null;
            entry.WorldData = null;
            entry.PoseData = null;
        }
        else
        {
            _poseList.Remove(entry);
        }

        UpdatePoseList();
    }

    /// <summary>
    /// Rebuilds the Poses list based on the working list and nulls the property when unchanged
    /// compared to the baseline.
    /// </summary>
    public void UpdatePoseList()
    {
        Poses = [.. _poseList];
        if (!Poses.Except(_charaDataFullDto.PoseData).Any() && !_charaDataFullDto.PoseData.Except(Poses).Any())
        {
            Poses = null;
        }
    }

    /// <summary>Sets the expiry to 7 days from now or clears it based on the flag.</summary>
    public void SetExpiry(bool expiring)
    {
        if (expiring)
        {
            var date = DateTime.UtcNow.AddDays(7);
            SetExpiry(date.Year, date.Month, date.Day);
        }
        else
        {
            ExpiryDate = DateTime.MaxValue;
        }
    }

    /// <summary>Sets the expiry date to a specific Y-M-D in UTC.</summary>
    public void SetExpiry(int year, int month, int day)
    {
        int daysInMonth = DateTime.DaysInMonth(year, month);
        if (day > daysInMonth) day = 1;
        ExpiryDate = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>
    /// Resets all changes back to the baseline by nulling changed properties and restoring
    /// working lists from the source data.
    /// </summary>
    internal void UndoChanges()
    {
        base.Description = null;
        base.AccessType = null;
        base.ShareType = null;
        base.GlamourerData = null;
        base.FileSwaps = null;
        base.FileGamePaths = null;
        base.CustomizeData = null;
        base.ManipulationData = null;
        AllowedUsers = null;
        AllowedGroups = null;
        Poses = null;
        _poseList.Clear();
        _poseList.AddRange(_charaDataFullDto.PoseData.Select(k => new PoseEntry(k.Id)
        {
            Description = k.Description,
            PoseData = k.PoseData,
            WorldData = k.WorldData
        }));
    }

    /// <summary>
    /// Reverts a deletion for an existing pose by restoring the original values from the baseline
    /// and updating the pose list diff.
    /// </summary>
    internal void RevertDeletion(PoseEntry pose)
    {
        if (pose.Id == null) return;
        var oldPose = _charaDataFullDto.PoseData.Find(p => p.Id == pose.Id);
        if (oldPose == null) return;
        pose.Description = oldPose.Description;
        pose.PoseData = oldPose.PoseData;
        pose.WorldData = oldPose.WorldData;
        UpdatePoseList();
    }

    /// <summary>
    /// Determines whether the provided pose differs from the baseline.
    /// </summary>
    internal bool PoseHasChanges(PoseEntry pose)
    {
        if (pose.Id == null) return false;
        var oldPose = _charaDataFullDto.PoseData.Find(p => p.Id == pose.Id);
        if (oldPose == null) return false;
        return !string.Equals(pose.Description, oldPose.Description, StringComparison.Ordinal)
            || !string.Equals(pose.PoseData, oldPose.PoseData, StringComparison.Ordinal)
            || pose.WorldData != oldPose.WorldData;
    }

    /// <summary>True when any property is currently set to be sent to the server.</summary>
    public bool HasChanges =>
                base.Description != null
                || base.ExpiryDate != null
                || base.AccessType != null
                || base.ShareType != null
                || AllowedUsers != null
                || AllowedGroups != null
                || base.GlamourerData != null
                || base.FileSwaps != null
                || base.FileGamePaths != null
                || base.CustomizeData != null
                || base.ManipulationData != null
                || Poses != null;

    /// <summary>Compares appearance-relevant fields to the baseline for UI convenience.</summary>
    public bool IsAppearanceEqual =>
        string.Equals(GlamourerData, _charaDataFullDto.GlamourerData, StringComparison.Ordinal)
        && string.Equals(CustomizeData, _charaDataFullDto.CustomizeData, StringComparison.Ordinal)
        && FileGamePaths == _charaDataFullDto.FileGamePaths
        && FileSwaps == _charaDataFullDto.FileSwaps
        && string.Equals(ManipulationData, _charaDataFullDto.ManipulationData, StringComparison.Ordinal);
}
