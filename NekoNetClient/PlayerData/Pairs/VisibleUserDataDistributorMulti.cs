using Microsoft.Extensions.Logging;
using NekoNet.API.Data;
using NekoNet.API.Dto;
using NekoNetClient.Services;
using NekoNetClient.Services.Mediator;
using NekoNetClient.WebAPI.Files;
using NekoNetClient.WebAPI.SignalR;
using System.Collections.Concurrent;
using NekoNetClient.Utils;

namespace NekoNetClient.PlayerData.Pairs;

public sealed class VisibleUserDataDistributorMulti : DisposableMediatorSubscriberBase
{
    private readonly MultiHubManager _multi;
    private readonly FileUploadManager _upload;
    private readonly DalamudUtilService _dalamud;

    private CharacterData? _lastCreatedData;
    private readonly SemaphoreSlim _pushLock = new(1, 1);

    // Track previous visible users per service and per configured server index
    private readonly ConcurrentDictionary<SyncService, HashSet<UserData>> _prevServiceVisible = new();
    private readonly ConcurrentDictionary<int, HashSet<UserData>> _prevConfiguredVisible = new();

    public VisibleUserDataDistributorMulti(ILogger<VisibleUserDataDistributorMulti> logger,
        MultiHubManager multi, FileUploadManager upload, DalamudUtilService dalamud,
        MareMediator mediator) : base(logger, mediator)
    {
        _multi = multi;
        _upload = upload;
        _dalamud = dalamud;

        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) =>
        {
            _lastCreatedData = msg.CharacterData;
            _ = PushToAllAsync(force: true);
        });

        Mediator.Subscribe<ConfiguredConnectedMessage>(this, (msg) =>
        {
            _ = PushToConfiguredAsync(msg.ServerIndex, force: true);
        });

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) => OnFrame());
    }

    private void OnFrame()
    {
        if (!_dalamud.GetIsPlayerPresent() || _lastCreatedData == null) return;

        // Services
        foreach (var svc in Enum.GetValues<SyncService>())
        {
            if (_multi.GetState(svc) != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected) continue;
            var pm = _multi.GetPairManagerForService(svc);
            var visible = pm.GetVisibleUsers();
            var prev = _prevServiceVisible.GetOrAdd(svc, _ => new HashSet<UserData>(NekoNet.API.Data.Comparer.UserDataComparer.Instance));
            var newVisible = visible.Where(u => !prev.Contains(u)).ToList();
            if (newVisible.Count > 0)
            {
                _ = PushToServiceAsync(svc);
                prev.Clear();
                foreach (var v in visible) prev.Add(v);
            }
        }

        // Configured servers (indices)
        for (int idx = 0; idx < 64; idx++)
        {
            // Skip unconnected indexes quickly
            if (_multi.GetConfiguredState(idx) != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected) continue;
            var pm = _multi.GetPairManagerForConfigured(idx);
            var visible = pm.GetVisibleUsers();
            var prev = _prevConfiguredVisible.GetOrAdd(idx, _ => new HashSet<UserData>(NekoNet.API.Data.Comparer.UserDataComparer.Instance));
            var newVisible = visible.Where(u => !prev.Contains(u)).ToList();
            if (newVisible.Count > 0)
            {
                _ = PushToConfiguredAsync(idx);
                prev.Clear();
                foreach (var v in visible) prev.Add(v);
            }
        }
    }

    private async Task PushToAllAsync(bool force)
    {
        await PushToServiceAsync(SyncService.NekoNet, force);
        await PushToServiceAsync(SyncService.Lightless, force);
        await PushToServiceAsync(SyncService.TeraSync, force);

        // Push to first 64 configured slots (cheap check gates by connection state)
        for (int idx = 0; idx < 64; idx++)
        {
            await PushToConfiguredAsync(idx, force);
        }
    }

    private async Task PushToServiceAsync(SyncService svc, bool force = false)
    {
        try
        {
            if (_lastCreatedData == null) return;
            if (_multi.GetState(svc) != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected) return;
            var pm = _multi.GetPairManagerForService(svc);
            var recipients = pm.GetVisibleUsers();
            if (recipients.Count == 0) return;

            await _pushLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var data = VariousExtensions.DeepClone(_lastCreatedData!);
                // Upload files against the appropriate CDN is handled inside FileUploadManager via global orchestrator (uses host mapping)
                await _upload.UploadFiles(data, recipients).ConfigureAwait(false);
                await _multi.PushCharacterDataAsync(svc, data, recipients).ConfigureAwait(false);
            }
            finally { _pushLock.Release(); }
        }
        catch { }
    }

    private async Task PushToConfiguredAsync(int serverIndex, bool force = false)
    {
        try
        {
            if (_lastCreatedData == null) return;
            if (_multi.GetConfiguredState(serverIndex) != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected) return;
            var pm = _multi.GetPairManagerForConfigured(serverIndex);
            var recipients = pm.GetVisibleUsers();
            if (recipients.Count == 0) return;

            await _pushLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var data = VariousExtensions.DeepClone(_lastCreatedData!);
                await _upload.UploadFiles(data, recipients, serverIndex).ConfigureAwait(false);
                await _multi.PushCharacterDataConfiguredAsync(serverIndex, data, recipients).ConfigureAwait(false);
            }
            finally { _pushLock.Release(); }
        }
        catch { }
    }
}
