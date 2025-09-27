/*
     Neko-Net Client — WebAPI.SignalR.ApiController (Groups)
     -------------------------------------------------------
     Purpose
     - Partial class containing group-related API operations (create, join/finalize, leave, delete, prune,
         ban/unban, permission changes, temp-invites, listing) that proxy to the SignalR hub. Each method asserts
         a valid connection via CheckConnection and then uses SendAsync/InvokeAsync with strongly typed DTOs.
     Notes
     - These are thin wrappers intended to keep the hub contract in one place and maintain a clear separation
         of concerns between the UI/services and the network transport.
*/
using Microsoft.AspNetCore.SignalR.Client;
using NekoNet.API.Dto.Group;
using NekoNetClient.WebAPI.SignalR.Utils;

namespace NekoNetClient.WebAPI.SignalR;

public partial class ApiController
{
    /// <summary>Bans a user from a group with a provided reason.</summary>
    public async Task GroupBanUser(GroupPairDto dto, string reason)
    {
        CheckConnection();
        await _mareHub!.SendAsync(nameof(GroupBanUser), dto, reason).ConfigureAwait(false);
    }

    /// <summary>Changes a group-wide permission state.</summary>
    public async Task GroupChangeGroupPermissionState(GroupPermissionDto dto)
    {
        CheckConnection();
        await _mareHub!.SendAsync(nameof(GroupChangeGroupPermissionState), dto).ConfigureAwait(false);
    }

    /// <summary>Changes an individual user's permission state within a group.</summary>
    public async Task GroupChangeIndividualPermissionState(GroupPairUserPermissionDto dto)
    {
        CheckConnection();
        await SetBulkPermissions(new(new(StringComparer.Ordinal),
            new(StringComparer.Ordinal) {
                { dto.Group.GID, dto.GroupPairPermissions }
            })).ConfigureAwait(false);
    }

    /// <summary>Transfers ownership of a group to another user.</summary>
    public async Task GroupChangeOwnership(GroupPairDto groupPair)
    {
        CheckConnection();
        await _mareHub!.SendAsync(nameof(GroupChangeOwnership), groupPair).ConfigureAwait(false);
    }

    /// <summary>Changes the password of the specified group.</summary>
    /// <returns>True if successfully changed, otherwise false.</returns>
    public async Task<bool> GroupChangePassword(GroupPasswordDto groupPassword)
    {
        CheckConnection();
        return await _mareHub!.InvokeAsync<bool>(nameof(GroupChangePassword), groupPassword).ConfigureAwait(false);
    }

    /// <summary>Clears the members/content of a group (server-defined semantics).</summary>
    public async Task GroupClear(GroupDto group)
    {
        CheckConnection();
        await _mareHub!.SendAsync(nameof(GroupClear), group).ConfigureAwait(false);
    }

    /// <summary>Creates a new group and returns the join payload.</summary>
    public async Task<GroupJoinDto> GroupCreate()
    {
        CheckConnection();
        return await _mareHub!.InvokeAsync<GroupJoinDto>(nameof(GroupCreate)).ConfigureAwait(false);
    }

    /// <summary>Creates a set of temporary invites for a group.</summary>
    public async Task<List<string>> GroupCreateTempInvite(GroupDto group, int amount)
    {
        CheckConnection();
        return await _mareHub!.InvokeAsync<List<string>>(nameof(GroupCreateTempInvite), group, amount).ConfigureAwait(false);
    }

    /// <summary>Deletes the specified group.</summary>
    public async Task GroupDelete(GroupDto group)
    {
        CheckConnection();
        await _mareHub!.SendAsync(nameof(GroupDelete), group).ConfigureAwait(false);
    }

    /// <summary>Gets the list of banned users from a group.</summary>
    public async Task<List<BannedGroupUserDto>> GroupGetBannedUsers(GroupDto group)
    {
        CheckConnection();
        return await _mareHub!.InvokeAsync<List<BannedGroupUserDto>>(nameof(GroupGetBannedUsers), group).ConfigureAwait(false);
    }

    /// <summary>Begins joining a group using a passworded invite.</summary>
    public async Task<GroupJoinInfoDto> GroupJoin(GroupPasswordDto passwordedGroup)
    {
        CheckConnection();
        return await _mareHub!.InvokeAsync<GroupJoinInfoDto>(nameof(GroupJoin), passwordedGroup).ConfigureAwait(false);
    }

    /// <summary>Finalizes joining a group after initial invite validation.</summary>
    /// <returns>True on success.</returns>
    public async Task<bool> GroupJoinFinalize(GroupJoinDto passwordedGroup)
    {
        CheckConnection();
        return await _mareHub!.InvokeAsync<bool>(nameof(GroupJoinFinalize), passwordedGroup).ConfigureAwait(false);
    }

    /// <summary>Leaves the specified group.</summary>
    public async Task GroupLeave(GroupDto group)
    {
        CheckConnection();
        await _mareHub!.SendAsync(nameof(GroupLeave), group).ConfigureAwait(false);
    }

    /// <summary>Removes a user from the group.</summary>
    public async Task GroupRemoveUser(GroupPairDto groupPair)
    {
        CheckConnection();
        await _mareHub!.SendAsync(nameof(GroupRemoveUser), groupPair).ConfigureAwait(false);
    }

    /// <summary>Sets per-user info (e.g., nickname) for a user in a group.</summary>
    public async Task GroupSetUserInfo(GroupPairUserInfoDto groupPair)
    {
        CheckConnection();
        await _mareHub!.SendAsync(nameof(GroupSetUserInfo), groupPair).ConfigureAwait(false);
    }

    /// <summary>Prunes inactive users from a group according to retention policy.</summary>
    /// <param name="days">Inactivity threshold in days.</param>
    /// <param name="execute">When false, returns the would-prune count (dry run).</param>
    /// <returns>Number of users pruned or would be pruned.</returns>
    public async Task<int> GroupPrune(GroupDto group, int days, bool execute)
    {
        CheckConnection();
        return await _mareHub!.InvokeAsync<int>(nameof(GroupPrune), group, days, execute).ConfigureAwait(false);
    }

    /// <summary>Retrieves all groups the current user is associated with.</summary>
    public async Task<List<GroupFullInfoDto>> GroupsGetAll()
    {
        CheckConnection();
        return await _mareHub!.InvokeAsync<List<GroupFullInfoDto>>(nameof(GroupsGetAll)).ConfigureAwait(false);
    }

    /// <summary>Unbans a previously banned user from a group.</summary>
    public async Task GroupUnbanUser(GroupPairDto groupPair)
    {
        CheckConnection();
        await _mareHub!.SendAsync(nameof(GroupUnbanUser), groupPair).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures that the hub connection is in a usable state before invoking a hub method.
    /// Throws <see cref="InvalidDataException"/> when not in Connected/Connecting/Reconnecting states.
    /// </summary>
    private void CheckConnection()
    {
        if (ServerState is not (ServerState.Connected or ServerState.Connecting or ServerState.Reconnecting)) throw new InvalidDataException("Not connected");
    }
}