//
// NekoNetClient — Services.Sync.RollingSyncRegistry
// ------------------------------------------------------------
// Purpose:
//   Tracks which UIDs are online across services so we avoid disposing
//   presence for users who remain online on another service. Also stores
//   the last seen player name per UID for diagnostics and UI.
//
using Microsoft.Extensions.Logging;
using NekoNetClient.Services.Mediator;
using System.Collections.Concurrent;

namespace NekoNetClient.Services.Sync;

/// <summary>
/// Registry of user presence across multiple services. Maintains a map of UID → service keys
/// where the user is currently online, and the last known player name for that UID.
/// </summary>
public class RollingSyncRegistry : IMediatorSubscriber
{
    private readonly ILogger<RollingSyncRegistry> _logger;
    private readonly ConcurrentDictionary<string, Entry> _byUid = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        /// <summary>Set of services where the UID is currently online.</summary>
        public ConcurrentDictionary<string, byte> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Last known player name for diagnostics/UI.</summary>
        public string? PlayerName { get; set; }
    }

    /// <summary>
    /// Creates a new rolling registry and subscribes to name updates.
    /// </summary>
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

    /// <summary>Gets the mediator used for subscriptions.</summary>
    public MareMediator Mediator { get; }

    /// <summary>
    /// Marks a UID as online on the specified service, optionally updating the player's name.
    /// </summary>
    public void Online(string uid, string serviceKey, string? playerName = null)
    {
        var e = _byUid.GetOrAdd(uid, _ => new Entry());
        e.Services[serviceKey] = 1;
        if (!string.IsNullOrEmpty(playerName)) e.PlayerName = playerName;
    }

    /// <summary>
    /// Marks a UID as offline on the specified service and removes the UID when no services remain.
    /// </summary>
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

    /// <summary>
    /// Returns true if the UID is online on any service other than the provided one.
    /// </summary>
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

    /// <summary>
    /// Produces a snapshot of the registry suitable for diagnostics or UI display.
    /// </summary>
    public IReadOnlyDictionary<string, (IReadOnlyCollection<string> Services, string? PlayerName)> Snapshot()
    {
        return _byUid.ToDictionary(kv => kv.Key,
            kv => ((IReadOnlyCollection<string>)kv.Value.Services.Keys.ToList(), kv.Value.PlayerName),
            StringComparer.Ordinal);
    }
}
