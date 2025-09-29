using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NekoNetClient.Services;

public interface IAppearancePresenceManager
{
    // Acquire/refresh a presence lease for this service on this player
    void Acquire(string serviceKey, string applyKey, string appearanceHash);
    // Release the lease. Returns true if no services remain (caller should unapply).
    bool Release(string serviceKey, string applyKey);
    // When a service disconnects, release all leases from that service and get applyKeys that reached zero.
    IReadOnlyList<string> ReleaseAllForService(string serviceKey);
    // Optional: update hash if appearance changes while still present
    void UpdateHash(string serviceKey, string applyKey, string appearanceHash);
    int GetRefCount(string applyKey);
}

public sealed class AppearancePresenceManager : IAppearancePresenceManager
{
    private readonly ILogger<AppearancePresenceManager> _log;

    private sealed class Entry
    {
        public HashSet<string> Services = new(StringComparer.OrdinalIgnoreCase);
        public string LastHash = string.Empty;
    }

    private readonly ConcurrentDictionary<string, Entry> _byApplyKey = new(StringComparer.OrdinalIgnoreCase);

    public AppearancePresenceManager(ILogger<AppearancePresenceManager> log) => _log = log;

    public void Acquire(string serviceKey, string applyKey, string appearanceHash)
    {
        var e = _byApplyKey.GetOrAdd(applyKey, _ => new Entry());
        lock (e)
        {
            e.Services.Add(serviceKey);
            e.LastHash = appearanceHash;
        }
        _log.LogTrace("Presence acquire {applyKey} by {serviceKey} (refs: {refs})", applyKey, serviceKey, GetRefCount(applyKey));
    }

    public bool Release(string serviceKey, string applyKey)
    {
        // If no entry exists for this applyKey, do not signal unapply
        if (!_byApplyKey.TryGetValue(applyKey, out var e)) return false;
        lock (e)
        {
            e.Services.Remove(serviceKey);
            if (e.Services.Count == 0)
            {
                _byApplyKey.TryRemove(applyKey, out _);
                _log.LogTrace("Presence zero for {applyKey} after release by {serviceKey}", applyKey, serviceKey);
                return true;
            }
        }
        _log.LogTrace("Presence keep {applyKey} after release by {serviceKey} (refs: {refs})", applyKey, serviceKey, e.Services.Count);
        return false;
    }

    public IReadOnlyList<string> ReleaseAllForService(string serviceKey)
    {
        var toUnapply = new List<string>();
        foreach (var kv in _byApplyKey)
        {
            var applyKey = kv.Key;
            var e = kv.Value;
            bool zero = false;
            lock (e)
            {
                if (e.Services.Remove(serviceKey) && e.Services.Count == 0)
                {
                    zero = true;
                }
            }
            if (zero)
            {
                _byApplyKey.TryRemove(applyKey, out _);
                toUnapply.Add(applyKey);
            }
        }
        _log.LogTrace("Service {serviceKey} released; will unapply {count} players with zero refs", serviceKey, toUnapply.Count);
        return toUnapply;
    }

    public void UpdateHash(string serviceKey, string applyKey, string appearanceHash)
    {
        var e = _byApplyKey.GetOrAdd(applyKey, _ => new Entry());
        lock (e)
        {
            e.Services.Add(serviceKey);
            e.LastHash = appearanceHash;
        }
    }

    public int GetRefCount(string applyKey)
        => _byApplyKey.TryGetValue(applyKey, out var e) ? e.Services.Count : 0;
}
