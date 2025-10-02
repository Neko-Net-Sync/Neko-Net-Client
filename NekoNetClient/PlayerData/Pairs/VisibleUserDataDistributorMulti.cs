/*
    Neko-Net Client — PlayerData.Pairs.VisibleUserDataDistributorMulti
    ------------------------------------------------------------------
    Purpose
    - Distributes your character data to visible users across multiple services and configured servers.
    - Coordinates uploads and pushes via MultiHubManager and FileUploadManager.

    Behavior
    - Reacts to CharacterDataCreated, ConfiguredConnected and per-frame events to discover recipients.
    - Tracks last visible sets per service and per configured index to avoid redundant pushes.
*/
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

/// <summary>
/// Multi-service variant of visible user data distribution. Handles both named services (e.g., NekoNet, Lightless,
/// TeraSync) and configured server indices, ensuring files are uploaded and data is pushed only to newly visible users.
/// </summary>
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
    // Track last pushed hash per service / configured index to push on changes regardless of visible deltas
    private readonly ConcurrentDictionary<SyncService, string> _lastPushedHashByService = new();
    private readonly ConcurrentDictionary<int, string> _lastPushedHashByConfigured = new();

    /// <summary>
    /// Creates a new multi-service distributor.
    /// </summary>
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
            // Invalidate last pushed state so we force a push on all connected targets
            foreach (var svc in Enum.GetValues<SyncService>()) _lastPushedHashByService.TryRemove(svc, out _);
            foreach (var k in _lastPushedHashByConfigured.Keys.ToList()) _lastPushedHashByConfigured.TryRemove(k, out _);
            _ = PushToAllAsync(force: true);
        });

        Mediator.Subscribe<ConfiguredConnectedMessage>(this, (msg) =>
        {
            _ = PushToConfiguredAsync(msg.ServerIndex, force: true);
        });

        // Targeted push when a single user comes online for a given scope
        Mediator.Subscribe<VisibleUserCameOnlineMessage>(this, (msg) =>
        {
            try
            {
                if (_lastCreatedData == null) return;
                var pm = _multi.GetPairManagerForService(msg.Service);
                // Only push if they are visible in this scope
                var isVisible = pm.GetVisibleUsers().Any(u => u.UID == msg.User.UID);
                if (!isVisible) return;
                _ = PushToServiceAsync(msg.Service, force: false, recipientsOverride: new List<UserData> { msg.User });
            }
            catch { }
        });
        Mediator.Subscribe<VisibleUserCameOnlineConfiguredMessage>(this, (msg) =>
        {
            try
            {
                if (_lastCreatedData == null) return;
                var pm = _multi.GetPairManagerForConfigured(msg.ServerIndex);
                var isVisible = pm.GetVisibleUsers().Any(u => u.UID == msg.User.UID);
                if (!isVisible) return;
                _ = PushToConfiguredAsync(msg.ServerIndex, force: false, recipientsOverride: new List<UserData> { msg.User });
            }
            catch { }
        });

        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) => OnFrame());
    }

    /// <summary>
    /// Frame update: detects newly visible users across services and configured indices and schedules pushes.
    /// </summary>
    private void OnFrame()
    {
        if (!_dalamud.GetIsPlayerPresent() || _lastCreatedData == null) return;
        var currentHash = _lastCreatedData?.DataHash?.Value ?? string.Empty;

        // Services
        foreach (var svc in Enum.GetValues<SyncService>())
        {
            if (_multi.GetState(svc) != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected) continue;
            var pm = _multi.GetPairManagerForService(svc);
            var visible = pm.GetVisibleUsers();
            var prev = _prevServiceVisible.GetOrAdd(svc, _ => new HashSet<UserData>(NekoNet.API.Data.Comparer.UserDataComparer.Instance));
            var newVisible = visible.Where(u => !prev.Contains(u)).ToList();
            var hashChanged = !_lastPushedHashByService.TryGetValue(svc, out var lastHash) || !string.Equals(lastHash, currentHash, StringComparison.Ordinal);
            if (newVisible.Count > 0 || hashChanged)
            {
                // If hash didn't change, push just to the new folks; if it changed, push to everyone.
                _ = PushToServiceAsync(svc, force: hashChanged, recipientsOverride: hashChanged ? null : newVisible);
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
            var hashChanged = !_lastPushedHashByConfigured.TryGetValue(idx, out var lastHash) || !string.Equals(lastHash, currentHash, StringComparison.Ordinal);
            if (newVisible.Count > 0 || hashChanged)
            {
                // If hash didn't change, push just to the new folks; if it changed, push to everyone.
                _ = PushToConfiguredAsync(idx, force: hashChanged, recipientsOverride: hashChanged ? null : newVisible);
                prev.Clear();
                foreach (var v in visible) prev.Add(v);
            }
        }
    }

    /// <summary>
    /// Pushes to all supported services and configured indices.
    /// </summary>
    private async Task PushToAllAsync(bool force)
    {
        await PushToServiceAsync(SyncService.NekoNet, force);
        await PushToServiceAsync(SyncService.Lightless, force);
        await PushToServiceAsync(SyncService.TeraSync, force);

        // Push to first 150 configured slots (cheap check gates by connection state)
        for (int idx = 0; idx < 150; idx++)
        {
            await PushToConfiguredAsync(idx, force);
        }
    }

    /// <summary>
    /// Pushes the current data to all visible users on a single service.
    /// </summary>
    private async Task PushToServiceAsync(SyncService svc, bool force = false, IReadOnlyList<UserData>? recipientsOverride = null)
    {
        try
        {
            if (_lastCreatedData == null) return;
            if (_multi.GetState(svc) != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected) return;
            var pm = _multi.GetPairManagerForService(svc);
            var recipientsRo = recipientsOverride ?? pm.GetVisibleUsers();
            var recipients = recipientsRo as List<UserData> ?? new List<UserData>(recipientsRo);
            if (recipients.Count == 0) return;
            var currentHash = _lastCreatedData?.DataHash?.Value ?? string.Empty;
            // If we have an explicit recipient override (e.g., newly visible users), bypass hash gating so they still get current data.
            if (recipientsOverride == null && !force && _lastPushedHashByService.TryGetValue(svc, out var lastHash) && string.Equals(lastHash, currentHash, StringComparison.Ordinal))
                return;

            await _pushLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var data = VariousExtensions.DeepClone(_lastCreatedData!);
                // Upload files against the appropriate CDN is handled inside FileUploadManager via global orchestrator (uses host mapping)
                await _upload.UploadFiles(data, recipients).ConfigureAwait(false);
                await _multi.PushCharacterDataAsync(svc, data, recipients).ConfigureAwait(false);
                _lastPushedHashByService[svc] = currentHash;
            }
            finally { _pushLock.Release(); }
        }
        catch { }
    }

    /// <summary>
    /// Pushes the current data to all visible users on a configured server index.
    /// </summary>
    private async Task PushToConfiguredAsync(int serverIndex, bool force = false, IReadOnlyList<UserData>? recipientsOverride = null)
    {
        try
        {
            if (_lastCreatedData == null) return;
            if (_multi.GetConfiguredState(serverIndex) != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected) return;
            var pm = _multi.GetPairManagerForConfigured(serverIndex);
            var recipientsRo = recipientsOverride ?? pm.GetVisibleUsers();
            var recipients = recipientsRo as List<UserData> ?? new List<UserData>(recipientsRo);
            if (recipients.Count == 0) return;
            var currentHash = _lastCreatedData?.DataHash?.Value ?? string.Empty;
            // If we have an explicit recipient override (e.g., newly visible users), bypass hash gating so they still get current data.
            if (recipientsOverride == null && !force && _lastPushedHashByConfigured.TryGetValue(serverIndex, out var lastHash) && string.Equals(lastHash, currentHash, StringComparison.Ordinal))
                return;

            await _pushLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var data = VariousExtensions.DeepClone(_lastCreatedData!);
                await _upload.UploadFiles(data, recipients, serverIndex).ConfigureAwait(false);
                await _multi.PushCharacterDataConfiguredAsync(serverIndex, data, recipients).ConfigureAwait(false);
                _lastPushedHashByConfigured[serverIndex] = currentHash;
            }
            finally { _pushLock.Release(); }
        }
        catch { }
    }
}
