using System.Collections.Concurrent;

namespace QuantifiedSelf.Windows.Agent.Services;

public sealed class ProcessedRequestCache
{
    private readonly ConcurrentDictionary<string, DateTime> _cache = new();
    private readonly int _capacity;
    private readonly TimeSpan _ttl;

    public ProcessedRequestCache(int capacity = 100, TimeSpan? ttl = null)
    {
        _capacity = capacity;
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// Returns true if this requestId has already been processed within the TTL.
    /// If not yet processed, adds it and returns false.
    /// </summary>
    public bool TryMarkProcessed(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        PurgeExpired();

        if (_cache.ContainsKey(requestId))
        {
            return true; // already processed
        }

        if (_cache.Count >= _capacity)
        {
            // Evict oldest entry to make room
            var oldest = _cache.OrderBy(kvp => kvp.Value).FirstOrDefault();
            if (!string.IsNullOrEmpty(oldest.Key))
            {
                _cache.TryRemove(oldest.Key, out _);
            }
        }

        _cache[requestId] = DateTime.UtcNow;
        return false;
    }

    private void PurgeExpired()
    {
        var cutoff = DateTime.UtcNow - _ttl;
        foreach (var kvp in _cache)
        {
            if (kvp.Value < cutoff)
            {
                _cache.TryRemove(kvp.Key, out _);
            }
        }
    }
}
