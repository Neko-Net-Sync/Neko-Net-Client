using Microsoft.Extensions.Logging;
using NekoNetClient.Services.Mediator;
using System.Collections.Concurrent;

namespace NekoNetClient.Services.Sync;

public class RollingSyncRegistry : IMediatorSubscriber
{
    private readonly ILogger<RollingSyncRegistry> _logger;
    private readonly ConcurrentDictionary<string, Entry> _byUid = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public ConcurrentDictionary<string, byte> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? PlayerName { get; set; }
    }

    public RollingSyncRegistry(ILogger<RollingSyncRegistry> logger, MareMediator mediator)
    {
        _logger = logger;
        Mediator = mediator;
        Mediator.Subscribe<PlayerNameKnownMessage>(this, msg =>
        {
            try
            {
                var e = _byUid.GetOrAdd(msg.UID, _ => new Entry());
                e.PlayerName = msg.PlayerName;
            }
            catch { }
        });
    }

    public MareMediator Mediator { get; }

    public void Online(string uid, string serviceKey, string? playerName = null)
    {
        var e = _byUid.GetOrAdd(uid, _ => new Entry());
        e.Services[serviceKey] = 1;
        if (!string.IsNullOrEmpty(playerName)) e.PlayerName = playerName;
    }

    public void Offline(string uid, string serviceKey)
    {
        if (_byUid.TryGetValue(uid, out var e))
        {
            e.Services.TryRemove(serviceKey, out _);
            if (e.Services.IsEmpty)
            {
                _byUid.TryRemove(uid, out _);
            }
        }
    }

    public bool IsOnlineElsewhere(string uid, string serviceKey)
    {
        if (_byUid.TryGetValue(uid, out var e))
        {
            foreach (var s in e.Services.Keys)
            {
                if (!string.Equals(s, serviceKey, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    public IReadOnlyDictionary<string, (IReadOnlyCollection<string> Services, string? PlayerName)> Snapshot()
    {
        return _byUid.ToDictionary(kv => kv.Key,
            kv => ((IReadOnlyCollection<string>)kv.Value.Services.Keys.ToList(), kv.Value.PlayerName),
            StringComparer.Ordinal);
    }
}
