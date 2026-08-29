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
    private readonly Dictionary<Guid, DateTimeOffset> _operationExpiry = [];
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
            _operationExpiry[id] = now + Retention;

            while (_operationExpiry.Count > MaximumEntries)
            {
                var oldest = _operationExpiry.MinBy(pair => pair.Value).Key;
                _operationExpiry.Remove(oldest);
            }
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
            _operationExpiry.Remove(id);
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
            return _operationExpiry.ContainsKey(id);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _operationExpiry.Clear();
        }
    }

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                PruneExpired(_utcNow());
                return _operationExpiry.Count;
            }
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var operationId in _operationExpiry
                     .Where(pair => pair.Value <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _operationExpiry.Remove(operationId);
        }
    }
}
