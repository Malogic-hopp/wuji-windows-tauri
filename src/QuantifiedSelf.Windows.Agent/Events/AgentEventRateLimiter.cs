namespace QuantifiedSelf.Windows.Agent.Events;

public sealed class AgentEventRateLimiter
{
    private readonly TimeSpan _window = TimeSpan.FromMinutes(5);
    private readonly int _limit = 5;
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTime>> _recentEvents = new(StringComparer.Ordinal);

    public bool ShouldAllow(string key, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            if (!_recentEvents.TryGetValue(key, out var queue))
            {
                queue = new Queue<DateTime>();
                _recentEvents[key] = queue;
            }

            while (queue.Count > 0 && utcNow - queue.Peek() > _window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= _limit)
            {
                return false;
            }

            queue.Enqueue(utcNow);
            return true;
        }
    }
}
