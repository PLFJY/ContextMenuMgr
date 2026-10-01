namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Tracks this frontend's recently submitted operations so their rebroadcast notifications
/// are not applied a second time through the long-lived subscription.
/// </summary>
internal sealed class RecentClientOperationCache
{
    private const int MaximumEntries = 256;
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(10);

    private readonly object _sync = new();
    private readonly Dictionary<Guid, (bool InFlight, DateTimeOffset Timestamp)> _operations = [];
    private readonly Func<DateTimeOffset> _utcNow;

    public RecentClientOperationCache(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public void Register(Guid? operationId)
    {
        if (operationId is not { } id || id == Guid.Empty)
        {
            return;
        }

        lock (_sync)
        {
            var now = _utcNow();
            PruneExpired(now);
            _operations[id] = (true, now);

            while (_operations.Count > MaximumEntries)
            {
                // Prefer evicting a completed entry. An in-flight entry must not
                // age out merely because its backend operation takes a long time.
                var oldest = _operations.OrderBy(pair => pair.Value.InFlight)
                    .ThenBy(pair => pair.Value.Timestamp).First().Key;
                _operations.Remove(oldest);
            }
        }
    }

    public void MarkCompleted(Guid? operationId)
    {
        if (operationId is not { } id || id == Guid.Empty) return;
        lock (_sync)
        {
            if (_operations.ContainsKey(id))
                _operations[id] = (false, _utcNow() + Retention);
        }
    }

    public void Remove(Guid? operationId)
    {
        if (operationId is not { } id || id == Guid.Empty)
        {
            return;
        }

        lock (_sync)
        {
            _operations.Remove(id);
        }
    }

    public bool Contains(Guid? operationId)
    {
        if (operationId is not { } id || id == Guid.Empty)
        {
            return false;
        }

        lock (_sync)
        {
            var now = _utcNow();
            PruneExpired(now);
            return _operations.ContainsKey(id);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _operations.Clear();
        }
    }

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                PruneExpired(_utcNow());
                return _operations.Count;
            }
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var operationId in _operations
                     .Where(pair => !pair.Value.InFlight && pair.Value.Timestamp <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _operations.Remove(operationId);
        }
    }
}
