using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using NekoNet.API.Data;
using NekoNet.API.Data.Enum;
using NekoNet.API.Dto;
using NekoNet.API.Dto.Group;
using NekoNet.API.Dto.User;
using NekoNetClient.MareConfiguration;
using NekoNetClient.PlayerData.Factories;
using NekoNetClient.PlayerData.Pairs;
using NekoNetClient.Services.Mediator;
using NekoNetClient.Services.Events;
using NekoNetClient.Services.ServerConfiguration;
using NekoNetClient.Utils;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NekoNetClient.WebAPI.SignalR
{
    public sealed class MultiHubManager : MediatorSubscriberBase, IAsyncDisposable
    {
        private readonly ILogger<MultiHubManager> _log;
        private readonly ServerConfigurationManager _servers;
        private readonly TokenProvider _tokens;
        private readonly PairManager _mainPairs;
        private readonly PairFactory _pairFactory;
        private readonly MareConfigService _cfg;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IContextMenu _contextMenu;

        private readonly ConcurrentDictionary<SyncService, HubConnection> _hubs = new();
        private readonly ConcurrentDictionary<SyncService, PairManager> _svcPairManagers = new();
        private readonly ConcurrentDictionary<SyncService, SystemInfoDto> _svcSystemInfo = new();
        private readonly ConcurrentDictionary<SyncService, string> _lastError = new();
        private readonly ConcurrentDictionary<SyncService, Uri> _svcCdn = new();
        private readonly ConcurrentDictionary<SyncService, DateTime> _svcLastPush = new();

        // Per-configured-server dictionaries (true multi-server)
        private readonly ConcurrentDictionary<int, HubConnection> _cfgHubs = new();
        private readonly ConcurrentDictionary<int, PairManager> _cfgPairManagers = new();
        private readonly ConcurrentDictionary<int, SystemInfoDto> _cfgSystemInfo = new();
        private readonly ConcurrentDictionary<int, string> _cfgLastError = new();
        private readonly ConcurrentDictionary<int, Uri> _cfgCdn = new();
        private readonly ConcurrentDictionary<int, DateTime> _cfgLastPush = new();

        public MultiHubManager(ILogger<MultiHubManager> logger,
            ServerConfigurationManager servers,
            TokenProvider tokens,
            PairManager pairs,
            MareMediator mediator,
            ILoggerFactory loggerFactory,
            PairFactory pairFactory,
            MareConfigService cfg,
            IContextMenu contextMenu)
            : base(logger, mediator)
        {
            _log = logger;
            _servers = servers;
            _tokens = tokens;
            _mainPairs = pairs;
            _loggerFactory = loggerFactory;
            _pairFactory = pairFactory;
            _cfg = cfg;
            _contextMenu = contextMenu;
        }

        public HubConnection? Get(SyncService svc) => _hubs.TryGetValue(svc, out var hub) ? hub : null;
        public HubConnectionState GetState(SyncService svc) => _hubs.TryGetValue(svc, out var hub) ? hub.State : HubConnectionState.Disconnected;
        public string GetResolvedUrl(SyncService svc) => SyncServiceSpecifications.Get(svc).HubEndpoint;
        public string? GetServiceCdnHost(SyncService svc)
            => _svcCdn.TryGetValue(svc, out var u) ? (u.IsDefaultPort ? u.Host : u.Host + ":" + u.Port) : null;
        public DateTime? GetServiceLastPushUtc(SyncService svc)
            => _svcLastPush.TryGetValue(svc, out var ts) ? ts : null;

        // Configured servers API
        public HubConnection? GetConfiguredHub(int serverIndex)
            => _cfgHubs.TryGetValue(serverIndex, out var hub) ? hub : null;
        public HubConnectionState GetConfiguredState(int serverIndex)
            => _cfgHubs.TryGetValue(serverIndex, out var hub) ? hub.State : HubConnectionState.Disconnected;
        public string? GetConfiguredCdnHost(int serverIndex)
            => _cfgCdn.TryGetValue(serverIndex, out var u) ? (u.IsDefaultPort ? u.Host : u.Host + ":" + u.Port) : null;
        public DateTime? GetConfiguredLastPushUtc(int serverIndex)
            => _cfgLastPush.TryGetValue(serverIndex, out var ts) ? ts : null;

        public string GetConfiguredResolvedUrl(int serverIndex)
        {
            var s = _servers.GetServerByIndex(serverIndex);
            try
            {
                var baseUri = new Uri(s.ServerUri);
                var path = s.ApiEndpoint ?? _servers.GetApiEndpointForDomain(s.ServerUri);
                var builder = new UriBuilder(baseUri) { Path = EnsureLeadingSlash(path) };
                return builder.Uri.ToString();
            }
            catch
            {
                return s.ServerUri;
            }
        }

        private static string EnsureLeadingSlash(string p)
            => string.IsNullOrWhiteSpace(p) ? "/mare" : (p[0] == '/' ? p : "/" + p);

        // Legacy/UI helper shims (no-op/minimal) to keep callers working
        public Task PrimeGroupListAsync(SyncService svc, CancellationToken ct) => Task.CompletedTask;

        public IReadOnlyList<GroupFullInfoDto> GetServiceGroupInfos(SyncService svc)
        {
            // Groups are applied directly into the PairManager; return empty for UI fallback seeding
            return Array.Empty<GroupFullInfoDto>();
        }

        public IReadOnlyList<OnlineUserIdentDto> GetServiceOnlinePairs(SyncService svc)
        {
            // Online status is pushed into the PairManager; return empty for UI fallback seeding
            return Array.Empty<OnlineUserIdentDto>();
        }

        public int? GetServerIndexForService(SyncService svc)
        {
            return _servers.FindServerIndexByService(svc);
        }

        public async Task<(int? Online, string Shard)> GetServiceOnlineAsync(SyncService svc, CancellationToken ct)
        {
            if (_svcSystemInfo.TryGetValue(svc, out var si))
                return (si.OnlineUsers, string.Empty);
            return (null, string.Empty);
        }

        public async Task<(int? Online, string Shard)> GetConfiguredOnlineAsync(int serverIndex, CancellationToken ct)
        {
            if (_cfgSystemInfo.TryGetValue(serverIndex, out var si))
                return (si.OnlineUsers, string.Empty);
            return (null, string.Empty);
        }

        // Removed legacy server profiles integration (JSON paths/protocol hints)

        // Minimal connection info (UID/defaults) with method-name fallbacks
        public async Task<ConnectionDto?> GetConnectionInfoAsync(SyncService svc, CancellationToken ct)
        {
            if (!_hubs.TryGetValue(svc, out var hub) || hub == null) return null;
            try
            {
                return await hub.InvokeAsync<ConnectionDto>("GetConnectionDto", cancellationToken: ct).ConfigureAwait(false);
            }
            catch
            {
                // Try alternative method names
                foreach (var alt in new[] { "GetConnectionInfo", "GetConnection" })
                {
                    try { return await hub.InvokeAsync<ConnectionDto>(alt, cancellationToken: ct).ConfigureAwait(false); }
                    catch { }
                }
                return null;
            }
        }

        public async Task<ConnectionDto?> GetConfiguredConnectionInfoAsync(int serverIndex, CancellationToken ct)
        {
            if (!_cfgHubs.TryGetValue(serverIndex, out var hub) || hub == null) return null;
            try
            {
                return await hub.InvokeAsync<ConnectionDto>("GetConnectionDto", cancellationToken: ct).ConfigureAwait(false);
            }
            catch
            {
                foreach (var alt in new[] { "GetConnectionInfo", "GetConnection" })
                {
                    try { return await hub.InvokeAsync<ConnectionDto>(alt, cancellationToken: ct).ConfigureAwait(false); }
                    catch { }
                }
                return null;
            }
        }

        public PairManager GetPairManagerForService(SyncService svc)
        {
            return _svcPairManagers.GetOrAdd(svc, s =>
            {
                var logger = _loggerFactory.CreateLogger<PairManager>();
                var apiBase = GetServiceApiBase(s);
                return new PairManager(logger, _pairFactory, _cfg, Mediator, _contextMenu, apiUrlOverride: apiBase, serviceScoped: true);
            });
        }

        private string GetServiceApiBase(SyncService svc)
        {
            try
            {
                return SyncServiceSpecifications.Get(svc).ApiBase;
            }
            catch
            {
                var current = _servers.CurrentApiUrl;
                return SyncServiceSpecifications.TrimTrailingSlashes(current);
            }
        }
        public PairManager GetPairManagerForConfigured(int serverIndex)
        {
            return _cfgPairManagers.GetOrAdd(serverIndex, idx =>
            {
                var logger = _loggerFactory.CreateLogger<PairManager>();
                var apiUrl = _servers.GetServerByIndex(idx).ServerUri;
                return new PairManager(logger, _pairFactory, _cfg, Mediator, _contextMenu, apiUrlOverride: apiUrl, serviceScoped: true);
            });
        }

        public Task DisconnectAsync(params SyncService[] services)
            => Task.WhenAll(services.Select(DisconnectAsync));

        public async Task DisconnectAsync(SyncService svc)
        {
            if (_hubs.TryGetValue(svc, out var hub))
            {
                try
                {
                    Mediator.Publish(new EventMessage(new Event(nameof(MultiHubManager), EventSeverity.Informational,
                        $"Disconnecting service {svc}") { Server = GetServiceApiBase(svc).ToServerLabel() }));
                }
                catch { }
                try { await hub.StopAsync().ConfigureAwait(false); } catch { }
            }
            _hubs.TryRemove(svc, out _);
            _lastError.TryRemove(svc, out _);
            _svcSystemInfo.TryRemove(svc, out _);
            if (_svcPairManagers.TryGetValue(svc, out var pm))
                pm.ClearPairs();
        }

        public async Task DisconnectConfiguredAsync(int serverIndex)
        {
            if (_cfgHubs.TryGetValue(serverIndex, out var hub))
            {
                try
                {
                    var s = _servers.GetServerByIndex(serverIndex);
                    Mediator.Publish(new EventMessage(new Event(nameof(MultiHubManager), EventSeverity.Informational,
                        $"Disconnecting configured server #{serverIndex}") { Server = s.ServerUri.ToServerLabel() }));
                }
                catch { }
                try { await hub.StopAsync().ConfigureAwait(false); } catch { }
            }
            _cfgHubs.TryRemove(serverIndex, out _);
            _cfgLastError.TryRemove(serverIndex, out _);
            _cfgSystemInfo.TryRemove(serverIndex, out _);
            if (_cfgPairManagers.TryGetValue(serverIndex, out var pm))
                pm.ClearPairs();
        }

        public Task ConnectAsync(params SyncService[] services)
            => Task.WhenAll(services.Select(ConnectAsync));

        public async Task ConnectAsync(SyncService svc)
        {
            var hub = await BuildHubAsync(svc, CancellationToken.None).ConfigureAwait(false);
            await hub.StartAsync().ConfigureAwait(false);
            _hubs[svc] = hub;
            await PostConnectBootstrapAsync(svc, hub).ConfigureAwait(false);
            try
            {
                var conn = await GetConnectionInfoAsync(svc, CancellationToken.None).ConfigureAwait(false);
                if (conn != null && conn.ServerInfo?.FileServerAddress != null)
                {
                    _svcCdn[svc] = conn.ServerInfo.FileServerAddress;
                    Mediator.Publish(new ServiceConnectedMessage(svc, conn, GetServiceApiBase(svc)));
                }
            }
            catch { }
        }

        public async Task ConnectConfiguredAsync(int serverIndex)
        {
            var hub = await BuildConfiguredHubAsync(serverIndex, CancellationToken.None).ConfigureAwait(false);
            await hub.StartAsync().ConfigureAwait(false);
            _cfgHubs[serverIndex] = hub;
            await PostConnectBootstrapConfiguredAsync(serverIndex, hub).ConfigureAwait(false);
            // Get ConnectionDto to obtain FileServerAddress for this configured server
            try
            {
                var conn = await GetConfiguredConnectionInfoAsync(serverIndex, CancellationToken.None).ConfigureAwait(false);
                if (conn != null)
                {
                    if (conn.ServerInfo?.FileServerAddress != null)
                        _cfgCdn[serverIndex] = conn.ServerInfo.FileServerAddress;
                    Mediator.Publish(new ConfiguredConnectedMessage(serverIndex, conn));
                }
            }
            catch { }
        }

        public async Task PushCharacterDataAsync(SyncService svc, CharacterData data, List<UserData> recipients, CensusDataDto? census = null)
        {
            if (!_hubs.TryGetValue(svc, out var hub) || hub == null) return;
            try
            {
                await hub.InvokeAsync("UserPushData", new UserCharaDataMessageDto(recipients, data, census)).ConfigureAwait(false);
                _svcLastPush[svc] = DateTime.UtcNow;
            }
            catch { }
        }

        public async Task PushCharacterDataConfiguredAsync(int serverIndex, CharacterData data, List<UserData> recipients, CensusDataDto? census = null)
        {
            if (!_cfgHubs.TryGetValue(serverIndex, out var hub) || hub == null) return;
            try
            {
                await hub.InvokeAsync("UserPushData", new UserCharaDataMessageDto(recipients, data, census)).ConfigureAwait(false);
                _cfgLastPush[serverIndex] = DateTime.UtcNow;
            }
            catch { }
        }

        private async Task<HubConnection> BuildHubAsync(SyncService svc, CancellationToken ct)
        {
            var spec = SyncServiceSpecifications.Get(svc);
            var resolvedIndex = GetServerIndexForService(svc);

            var builder = new HubConnectionBuilder()
                .WithUrl(spec.HubEndpoint, opt =>
                {
                    opt.Transports = HttpTransportType.WebSockets;
                    opt.SkipNegotiation = spec.RequiresWebSockets;
                    opt.AccessTokenProvider = resolvedIndex.HasValue
                        ? () => _tokens.GetOrUpdateTokenForServer(resolvedIndex.Value, ct)
                        : () => _tokens.GetOrUpdateToken(ct);
                })
                .WithAutomaticReconnect();

            var hub = builder.Build();

            // System info event for online/shard
            hub.On<SystemInfoDto>("Client_UpdateSystemInfo", dto =>
            {
                _svcSystemInfo[svc] = dto;
            });

            RegisterMareEventHandlers(svc, hub);

            hub.Closed += ex => { if (ex != null) _lastError[svc] = ex.Message; return Task.CompletedTask; };
            hub.Reconnecting += ex => { if (ex != null) _lastError[svc] = ex.Message; return Task.CompletedTask; };
            hub.Reconnected += id => { _lastError.TryRemove(svc, out _); return Task.CompletedTask; };

            return hub;
        }

        private async Task<HubConnection> BuildConfiguredHubAsync(int serverIndex, CancellationToken ct)
        {
            var s = _servers.GetServerByIndex(serverIndex);
            var endpoint = GetConfiguredResolvedUrl(serverIndex);
            var builder = new HubConnectionBuilder()
                .WithUrl(endpoint, opt =>
                {
                    opt.Transports = HttpTransportType.WebSockets;
                    opt.SkipNegotiation = s.ForceWebSockets;
                    opt.AccessTokenProvider = () => _tokens.GetOrUpdateTokenForServer(serverIndex, ct);
                })
                .WithAutomaticReconnect();

            var hub = builder.Build();

            hub.On<SystemInfoDto>("Client_UpdateSystemInfo", dto => { _cfgSystemInfo[serverIndex] = dto; });
            RegisterMareEventHandlersConfigured(serverIndex, hub);

            hub.Closed += ex => { if (ex != null) _cfgLastError[serverIndex] = ex.Message; return Task.CompletedTask; };
            hub.Reconnecting += ex => { if (ex != null) _cfgLastError[serverIndex] = ex.Message; return Task.CompletedTask; };
            hub.Reconnected += id => { _cfgLastError.TryRemove(serverIndex, out _); return Task.CompletedTask; };

            return hub;
        }

        private void RegisterMareEventHandlers(SyncService svc, HubConnection hub)
        {
            var pm = GetPairManagerForService(svc);
            // Users
            hub.On<UserPairDto>("Client_UserAddClientPair", dto => { try { pm.AddUserPair(dto, addToLastAddedUser: false); } catch { } });
            hub.On<UserDto>("Client_UserRemoveClientPair", dto => { try { pm.RemoveUserPair(dto); } catch { } });
            hub.On<OnlineUserIdentDto>("Client_UserSendOnline", dto =>
            {
                try { pm.MarkPairOnline(dto, sendNotif: false); } catch { }
            });
            hub.On<UserDto>("Client_UserSendOffline", dto => { try { pm.MarkPairOffline(dto.User); } catch { } });
            hub.On<OnlineUserCharaDataDto>("Client_UserReceiveCharacterData", dto => { try { pm.ReceiveCharaData(dto); } catch { } });
            hub.On<UserPermissionsDto>("Client_UserUpdateOtherPairPermissions", dto => { try { pm.UpdatePairPermissions(dto); } catch { } });
            hub.On<UserPermissionsDto>("Client_UserUpdateSelfPairPermissions", dto => { try { pm.UpdateSelfPairPermissions(dto); } catch { } });
            hub.On<UserIndividualPairStatusDto>("Client_UpdateUserIndividualPairStatusDto", dto => { try { pm.UpdateIndividualPairStatus(dto); } catch { } });

            // Groups
            hub.On<GroupFullInfoDto>("Client_GroupSendFullInfo", dto =>
            {
                try { pm.AddGroup(dto); } catch { }
                try
                {
                    foreach (var uid in dto.GroupPairUserInfos.Keys)
                    {
                        var minimal = new UserFullPairDto(new UserData(uid, string.Empty), IndividualPairStatus.None,
                            new List<string> { dto.Group.GID }, UserPermissions.NoneSet, UserPermissions.NoneSet);
                        pm.AddUserPair(minimal);
                    }
                }
                catch { }
            });
            hub.On<GroupInfoDto>("Client_GroupSendInfo", dto => { try { pm.SetGroupInfo(dto); } catch { } });
            hub.On<GroupPairFullInfoDto>("Client_GroupPairJoined", dto => { try { pm.AddGroupPair(dto); } catch { } });
            hub.On<GroupPairDto>("Client_GroupPairLeft", dto => { try { pm.RemoveGroupPair(dto); } catch { } });
            hub.On<GroupDto>("Client_GroupDelete", dto => { try { pm.RemoveGroup(dto.Group); } catch { } });
        }

        private void RegisterMareEventHandlersConfigured(int serverIndex, HubConnection hub)
        {
            var pm = GetPairManagerForConfigured(serverIndex);
            hub.On<UserPairDto>("Client_UserAddClientPair", dto => { try { pm.AddUserPair(dto, addToLastAddedUser: false); } catch { } });
            hub.On<UserDto>("Client_UserRemoveClientPair", dto => { try { pm.RemoveUserPair(dto); } catch { } });
            hub.On<OnlineUserIdentDto>("Client_UserSendOnline", dto => { try { pm.MarkPairOnline(dto, sendNotif: false); } catch { } });
            hub.On<UserDto>("Client_UserSendOffline", dto => { try { pm.MarkPairOffline(dto.User); } catch { } });
            hub.On<OnlineUserCharaDataDto>("Client_UserReceiveCharacterData", dto => { try { pm.ReceiveCharaData(dto); } catch { } });
            hub.On<UserPermissionsDto>("Client_UserUpdateOtherPairPermissions", dto => { try { pm.UpdatePairPermissions(dto); } catch { } });
            hub.On<UserPermissionsDto>("Client_UserUpdateSelfPairPermissions", dto => { try { pm.UpdateSelfPairPermissions(dto); } catch { } });
            hub.On<UserIndividualPairStatusDto>("Client_UpdateUserIndividualPairStatusDto", dto => { try { pm.UpdateIndividualPairStatus(dto); } catch { } });

            hub.On<GroupFullInfoDto>("Client_GroupSendFullInfo", dto =>
            {
                try { pm.AddGroup(dto); } catch { }
                try
                {
                    foreach (var uid in dto.GroupPairUserInfos.Keys)
                    {
                        var minimal = new UserFullPairDto(new UserData(uid, string.Empty), IndividualPairStatus.None,
                            new List<string> { dto.Group.GID }, UserPermissions.NoneSet, UserPermissions.NoneSet);
                        pm.AddUserPair(minimal);
                    }
                }
                catch { }
            });
            hub.On<GroupInfoDto>("Client_GroupSendInfo", dto => { try { pm.SetGroupInfo(dto); } catch { } });
            hub.On<GroupPairFullInfoDto>("Client_GroupPairJoined", dto => { try { pm.AddGroupPair(dto); } catch { } });
            hub.On<GroupPairDto>("Client_GroupPairLeft", dto => { try { pm.RemoveGroupPair(dto); } catch { } });
            hub.On<GroupDto>("Client_GroupDelete", dto => { try { pm.RemoveGroup(dto.Group); } catch { } });
        }

        private async Task PostConnectBootstrapAsync(SyncService svc, HubConnection hub)
        {
            var pm = GetPairManagerForService(svc);
            try
            {
                // Pairs
                List<UserFullPairDto>? pairsFull = null;
                try { pairsFull = await hub.InvokeAsync<List<UserFullPairDto>>("UserGetPairedClients").ConfigureAwait(false); }
                catch { }
                if (pairsFull != null)
                {
                    foreach (var p in pairsFull) { try { pm.AddUserPair(p); } catch { } }
                }
                else
                {
                    try
                    {
                        var pairsSimple = await hub.InvokeAsync<List<UserPairDto>>("UserGetPairedClients").ConfigureAwait(false);
                        if (pairsSimple != null)
                            foreach (var p in pairsSimple) { try { pm.AddUserPair(p); } catch { } }
                    }
                    catch { }
                }

                // Groups
                try
                {
                    List<GroupFullInfoDto>? groups = null;
                    try { groups = await hub.InvokeAsync<List<GroupFullInfoDto>>("GroupsGetAll").ConfigureAwait(false); } catch { }
                    if (groups == null)
                    {
                        foreach (var alt in new[] { "GetGroups", "Groups_All" })
                        {
                            try { groups = await hub.InvokeAsync<List<GroupFullInfoDto>>(alt).ConfigureAwait(false); break; } catch { }
                        }
                    }
                    if (groups != null)
                    {
                        foreach (var g in groups)
                        {
                            try { pm.AddGroup(g); } catch { }
                            foreach (var uid in g.GroupPairUserInfos.Keys)
                            {
                                var minimal = new UserFullPairDto(new UserData(uid, string.Empty), IndividualPairStatus.None,
                                    new List<string> { g.Group.GID }, UserPermissions.NoneSet, UserPermissions.NoneSet);
                                try { pm.AddUserPair(minimal); } catch { }
                            }
                        }
                    }
                }
                catch { }

                // Online
                try
                {
                    List<OnlineUserIdentDto>? online = null;
                    try { online = await hub.InvokeAsync<List<OnlineUserIdentDto>>("UserGetOnlinePairs", (object?)null).ConfigureAwait(false); } catch { }
                    if (online == null)
                    {
                        foreach (var alt in new[] { "GetOnlinePairs", "UserGetOnline" })
                        {
                            try { online = await hub.InvokeAsync<List<OnlineUserIdentDto>>(alt, (object?)null).ConfigureAwait(false); break; } catch { }
                        }
                    }
                    if (online != null)
                    {
                        foreach (var o in online)
                        {
                            try { pm.MarkPairOnline(o, sendNotif: false); } catch { }
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PostConnectBootstrap failed for {svc}", svc);
            }
        }

        private async Task PostConnectBootstrapConfiguredAsync(int serverIndex, HubConnection hub)
        {
            var pm = GetPairManagerForConfigured(serverIndex);
            try
            {
                List<UserFullPairDto>? pairsFull = null;
                try { pairsFull = await hub.InvokeAsync<List<UserFullPairDto>>("UserGetPairedClients").ConfigureAwait(false); } catch { }
                if (pairsFull != null)
                {
                    foreach (var p in pairsFull) { try { pm.AddUserPair(p); } catch { } }
                }
                else
                {
                    try
                    {
                        var pairsSimple = await hub.InvokeAsync<List<UserPairDto>>("UserGetPairedClients").ConfigureAwait(false);
                        if (pairsSimple != null)
                            foreach (var p in pairsSimple) { try { pm.AddUserPair(p); } catch { } }
                    }
                    catch { }
                }

                try
                {
                    List<GroupFullInfoDto>? groups = null;
                    try { groups = await hub.InvokeAsync<List<GroupFullInfoDto>>("GroupsGetAll").ConfigureAwait(false); } catch { }
                    if (groups == null)
                    {
                        foreach (var alt in new[] { "GetGroups", "Groups_All" })
                        {
                            try { groups = await hub.InvokeAsync<List<GroupFullInfoDto>>(alt).ConfigureAwait(false); break; } catch { }
                        }
                    }
                    if (groups != null)
                    {
                        foreach (var g in groups)
                        {
                            try { pm.AddGroup(g); } catch { }
                            foreach (var uid in g.GroupPairUserInfos.Keys)
                            {
                                var minimal = new UserFullPairDto(new UserData(uid, string.Empty), IndividualPairStatus.None,
                                    new List<string> { g.Group.GID }, UserPermissions.NoneSet, UserPermissions.NoneSet);
                                try { pm.AddUserPair(minimal); } catch { }
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    List<OnlineUserIdentDto>? online = null;
                    try { online = await hub.InvokeAsync<List<OnlineUserIdentDto>>("UserGetOnlinePairs", (object?)null).ConfigureAwait(false); } catch { }
                    if (online == null)
                    {
                        foreach (var alt in new[] { "GetOnlinePairs", "UserGetOnline" })
                        {
                            try { online = await hub.InvokeAsync<List<OnlineUserIdentDto>>(alt, (object?)null).ConfigureAwait(false); break; } catch { }
                        }
                    }
                    if (online != null)
                    {
                        foreach (var o in online)
                        {
                            try { pm.MarkPairOnline(o, sendNotif: false); } catch { }
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PostConnectBootstrap (configured) failed for {idx}", serverIndex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var hub in _hubs.Values)
            {
                try { await hub.DisposeAsync(); } catch { }
            }
            _hubs.Clear();
        }
    }
}
