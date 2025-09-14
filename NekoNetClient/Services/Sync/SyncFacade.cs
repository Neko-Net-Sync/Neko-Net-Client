using Microsoft.Extensions.Logging;
using NekoNetClient.PlayerData.Pairs;
using NekoNetClient.Services.Mediator;
using NekoNetClient.WebAPI.SignalR;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NekoNetClient.Services.Sync;

public sealed class SyncFacade : DisposableMediatorSubscriberBase, ISyncFacade
{
    private readonly ApiController _api;
    private readonly PairManager _pairs;
    private readonly Channel<SyncEvent> _events;

    public SyncFacade(ILogger<SyncFacade> logger, MareMediator mediator, ApiController api, PairManager pairs)
        : base(logger, mediator)
    {
        _api = api;
        _pairs = pairs;
        _events = Channel.CreateUnbounded<SyncEvent>();

        void Enqueue(SyncEvent ev) => _ = _events.Writer.TryWrite(ev);

        Mediator.Subscribe<ConnectedMessage>(this, _ => Enqueue(new SyncEvent(SyncEventKind.Connected, _api.DisplayName)));
        Mediator.Subscribe<DisconnectedMessage>(this, _ => Enqueue(new SyncEvent(SyncEventKind.Disconnected)));
        Mediator.Subscribe<HubReconnectingMessage>(this, _ => Enqueue(new SyncEvent(SyncEventKind.Reconnecting)));
        Mediator.Subscribe<HubReconnectedMessage>(this, _ => Enqueue(new SyncEvent(SyncEventKind.Reconnected)));
        Mediator.Subscribe<HubClosedMessage>(this, _ => Enqueue(new SyncEvent(SyncEventKind.Closed)));
        Mediator.Subscribe<RefreshUiMessage>(this, _ => Enqueue(new SyncEvent(SyncEventKind.UiRefresh)));
    }

    public bool IsReady => _api.IsConnected;

    public async Task StartAsync(CancellationToken ct)
    {
        // ApiController manages its own internal cancellation; just start.
        await _api.CreateConnectionsAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _api.StopConnectionsAsync().ConfigureAwait(false);
    }

    public async IAsyncEnumerable<SyncEvent> EventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SyncEvent ev;
            try
            {
                if (!await _events.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) yield break;
                if (!_events.Reader.TryRead(out ev)) continue;
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            yield return ev;
        }
    }

    public Task<IReadOnlyList<UserPairSummary>> GetPairsAsync(CancellationToken ct)
    {
        var list = _pairs.DirectPairs
            .Select(p => new UserPairSummary(p.UserData.UID, p.PlayerName ?? string.Empty, p.IsOnline, p.IsPaused))
            .ToList();

        return Task.FromResult<IReadOnlyList<UserPairSummary>>(list);
    }

    public Task<SyncStatus> GetStatusAsync(CancellationToken ct)
    {
        var si = _api.ServerInfo;
        var status = new SyncStatus(
            ShardName: si.ShardName ?? string.Empty,
            DisplayName: _api.DisplayName,
            Uid: _api.UID,
            IsReady: _api.IsConnected,
            State: _api.ServerState.ToString(),
            OnlineUsers: _api.OnlineUsers,
            ClientVersion: _api.CurrentClientVersion.ToString(),
            IsCurrentVersion: _api.IsCurrentVersion
        );

        return Task.FromResult(status);
    }
}
