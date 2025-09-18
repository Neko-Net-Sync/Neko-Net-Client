using NekoNet.API.Dto;
using NekoNet.API.Dto.Group;
using NekoNet.API.Dto.User;
using NekoNetClient.WebAPI.SignalR;
using System.Threading;
using Microsoft.AspNetCore.SignalR.Client;
using System;
using NekoNet.API.Data.Extensions;

namespace NekoNetClient.UI.Components;

public interface IApiActionRouter
{
    string UID { get; }
    DefaultPermissionsDto? DefaultPermissions { get; }

    Task UserAddPair(UserDto user);
    Task UserRemovePair(UserDto user);
    Task UserSetPairPermissions(UserPermissionsDto permissions);
    Task SetBulkPermissions(BulkPermissionsDto dto);

    Task GroupSetUserInfo(GroupPairUserInfoDto dto);
    Task GroupRemoveUser(GroupPairDto dto);
    Task GroupChangeOwnership(GroupPairDto dto);
    Task GroupChangeIndividualPermissionState(GroupPairUserPermissionDto dto);
    Task GroupLeave(GroupDto group);
}

internal sealed class MainApiActionRouter : IApiActionRouter
{
    private readonly ApiController _api;
    public MainApiActionRouter(ApiController api) { _api = api; }
    public string UID => _api.UID;
    public DefaultPermissionsDto? DefaultPermissions => _api.DefaultPermissions;
    public Task UserAddPair(UserDto user) => _api.UserAddPair(user);
    public Task UserRemovePair(UserDto user) => _api.UserRemovePair(user);
    public Task UserSetPairPermissions(UserPermissionsDto permissions) => _api.UserSetPairPermissions(permissions);
    public Task SetBulkPermissions(BulkPermissionsDto dto) => _api.SetBulkPermissions(dto);
    public Task GroupSetUserInfo(GroupPairUserInfoDto dto) => _api.GroupSetUserInfo(dto);
    public Task GroupRemoveUser(GroupPairDto dto) => _api.GroupRemoveUser(dto);
    public Task GroupChangeOwnership(GroupPairDto dto) => _api.GroupChangeOwnership(dto);
    public Task GroupChangeIndividualPermissionState(GroupPairUserPermissionDto dto) => _api.GroupChangeIndividualPermissionState(dto);
    public Task GroupLeave(GroupDto group) => _api.GroupLeave(group);
}

internal sealed class ServiceApiActionRouter : IApiActionRouter
{
    private readonly MultiHubManager _multi;
    private readonly SyncService _svc;
    private DefaultPermissionsDto? _defaults;
    private string _uid = string.Empty;
    private DateTime _lastInfoTs = DateTime.MinValue;

    public ServiceApiActionRouter(MultiHubManager multi, SyncService svc)
    {
        _multi = multi; _svc = svc;
    }

    private async Task EnsureConnInfoAsync()
    {
        if ((DateTime.UtcNow - _lastInfoTs) < TimeSpan.FromSeconds(10)) return;
        try
        {
            var dto = await _multi.GetConnectionInfoAsync(_svc, CancellationToken.None).ConfigureAwait(false);
            if (dto != null)
            {
                _uid = dto.User.UID;
                _defaults = dto.DefaultPreferredPermissions;
                _lastInfoTs = DateTime.UtcNow;
            }
        }
        catch { }
    }

    public string UID
    {
        get { _ = EnsureConnInfoAsync(); return _uid; }
    }

    public DefaultPermissionsDto? DefaultPermissions
    {
        get { _ = EnsureConnInfoAsync(); return _defaults; }
    }

    private HubConnection? Hub => _multi.Get(_svc);

    public async Task UserAddPair(UserDto user)
        => await (Hub?.SendAsync("UserAddPair", user) ?? Task.CompletedTask).ConfigureAwait(false);

    public async Task UserRemovePair(UserDto user)
        => await (Hub?.SendAsync("UserRemovePair", user) ?? Task.CompletedTask).ConfigureAwait(false);

    public async Task UserSetPairPermissions(UserPermissionsDto permissions)
    {
        try
        {
            // Defer while target is applying to avoid clashing with mid-apply redraw/collection changes
            try
            {
                var pm = _multi.GetPairManagerForService(_svc);
                var pair = pm.GetPairByUID(permissions.User.UID);
                if (pair != null && pair.IsApplying)
                {
                    var start = DateTime.UtcNow;
                    while (pair.IsApplying && (DateTime.UtcNow - start) < TimeSpan.FromSeconds(2))
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                    }
                }
                // Proactively revert visuals on pause so the user returns to vanilla immediately
                if (pair != null && permissions.Permissions.IsPaused())
                {
                    pair.MarkOffline(wait: false, reason: "Paused — local visual cleanup triggered");
                }
            }
            catch { }

            await SetBulkPermissions(new(new(StringComparer.Ordinal) { { permissions.User.UID, permissions.Permissions } }, new(StringComparer.Ordinal))).ConfigureAwait(false);
        }
        catch
        {
            // swallow exceptions to avoid unobserved Task crashes when callers do not await
        }
    }

    public async Task SetBulkPermissions(BulkPermissionsDto dto)
    {
        var hub = Hub;
        if (hub == null) return;
        try
        {
            // Try Mare-compatible signature (returns bool)
            _ = await hub.InvokeAsync<bool>("SetBulkPermissions", dto).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                // Fallback for servers that return void or different type
                await hub.SendAsync("SetBulkPermissions", dto).ConfigureAwait(false);
            }
            catch
            {
                // Final fallback: ignore to prevent unobserved exceptions
            }
        }
    }

    public async Task GroupSetUserInfo(GroupPairUserInfoDto dto)
        => await (Hub?.SendAsync("GroupSetUserInfo", dto) ?? Task.CompletedTask).ConfigureAwait(false);

    public async Task GroupRemoveUser(GroupPairDto dto)
        => await (Hub?.SendAsync("GroupRemoveUser", dto) ?? Task.CompletedTask).ConfigureAwait(false);

    public async Task GroupChangeOwnership(GroupPairDto dto)
        => await (Hub?.SendAsync("GroupChangeOwnership", dto) ?? Task.CompletedTask).ConfigureAwait(false);

    public async Task GroupChangeIndividualPermissionState(GroupPairUserPermissionDto dto)
        => await SetBulkPermissions(new(new(StringComparer.Ordinal), new(StringComparer.Ordinal) { { dto.Group.GID, dto.GroupPairPermissions } })).ConfigureAwait(false);

    public async Task GroupLeave(GroupDto group)
        => await (Hub?.SendAsync("GroupLeave", group) ?? Task.CompletedTask).ConfigureAwait(false);
}

// Per-configured-server action router (by server index)
internal sealed class ConfiguredApiActionRouter : IApiActionRouter
{
    private readonly MultiHubManager _multi;
    private readonly int _serverIndex;
    private DefaultPermissionsDto? _defaults;
    private string _uid = string.Empty;
    private DateTime _lastInfoTs = DateTime.MinValue;

    public ConfiguredApiActionRouter(MultiHubManager multi, int serverIndex)
    {
        _multi = multi; _serverIndex = serverIndex;
    }

    private async Task EnsureConnInfoAsync()
    {
        if ((DateTime.UtcNow - _lastInfoTs) < TimeSpan.FromSeconds(10)) return;
        try
        {
            var dto = await _multi.GetConfiguredConnectionInfoAsync(_serverIndex, CancellationToken.None).ConfigureAwait(false);
            if (dto != null)
            {
                _uid = dto.User.UID;
                _defaults = dto.DefaultPreferredPermissions;
                _lastInfoTs = DateTime.UtcNow;
            }
        }
        catch { }
    }

    public string UID { get { _ = EnsureConnInfoAsync(); return _uid; } }
    public DefaultPermissionsDto? DefaultPermissions { get { _ = EnsureConnInfoAsync(); return _defaults; } }

    private HubConnection? Hub => _multi.GetConfiguredHub(_serverIndex);

    public async Task UserAddPair(UserDto user)
        => await (Hub?.SendAsync("UserAddPair", user) ?? Task.CompletedTask).ConfigureAwait(false);

    public async Task UserRemovePair(UserDto user)
        => await (Hub?.SendAsync("UserRemovePair", user) ?? Task.CompletedTask).ConfigureAwait(false);

    public async Task UserSetPairPermissions(UserPermissionsDto permissions)
    {
        try
        {
            // Safe pause: if target pair is applying, wait briefly; and mark for deferred cleanup when pausing
            try
            {
                var pm = _multi.GetPairManagerForConfigured(_serverIndex);
                var pair = pm.GetPairByUID(permissions.User.UID);
                if (pair != null)
                {
                    if (pair.IsApplying)
                    {
                        var start = DateTime.UtcNow;
                        while (pair.IsApplying && (DateTime.UtcNow - start) < TimeSpan.FromSeconds(2))
                            await Task.Delay(100).ConfigureAwait(false);
                    }
                    if (permissions.Permissions.IsPaused())
                    {
                        // Proactive local cleanup on pause
                        pair.MarkOffline(wait: false, reason: "Paused — local visual cleanup triggered");
                    }
                }
            }
            catch { }

            await SetBulkPermissions(new(new(StringComparer.Ordinal) { { permissions.User.UID, permissions.Permissions } }, new(StringComparer.Ordinal))).ConfigureAwait(false);
        }
        catch { }
    }

    public async Task SetBulkPermissions(BulkPermissionsDto dto)
    {
        var hub = Hub; if (hub == null) return;
        try
        {
            _ = await hub.InvokeAsync<bool>("SetBulkPermissions", dto).ConfigureAwait(false);
        }
        catch
        {
            try { await hub.SendAsync("SetBulkPermissions", dto).ConfigureAwait(false); }
            catch { }
        }
    }

    public async Task GroupSetUserInfo(GroupPairUserInfoDto dto)
        => await (Hub?.SendAsync("GroupSetUserInfo", dto) ?? Task.CompletedTask).ConfigureAwait(false);

    public async Task GroupRemoveUser(GroupPairDto dto)
        => await (Hub?.SendAsync("GroupRemoveUser", dto) ?? Task.CompletedTask).ConfigureAwait(false);

    public async Task GroupChangeOwnership(GroupPairDto dto)
        => await (Hub?.SendAsync("GroupChangeOwnership", dto) ?? Task.CompletedTask).ConfigureAwait(false);

    public async Task GroupChangeIndividualPermissionState(GroupPairUserPermissionDto dto)
        => await SetBulkPermissions(new(new(StringComparer.Ordinal), new(StringComparer.Ordinal) { { dto.Group.GID, dto.GroupPairPermissions } })).ConfigureAwait(false);

    public async Task GroupLeave(GroupDto group)
        => await (Hub?.SendAsync("GroupLeave", group) ?? Task.CompletedTask).ConfigureAwait(false);
}

