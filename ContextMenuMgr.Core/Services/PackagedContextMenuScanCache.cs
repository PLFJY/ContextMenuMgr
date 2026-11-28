namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Shares expensive package discovery between snapshots for the same interactive user.
/// Blocked-list state is deliberately not cached and is read for every projection.
/// </summary>
internal sealed class PackagedContextMenuScanCache
{
    private readonly Func<string, IReadOnlyList<PackagedContextMenuDefinition>> _scan;
    private readonly TimeSpan _lifetime;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public PackagedContextMenuScanCache(
        Func<string, IReadOnlyList<PackagedContextMenuDefinition>> scan,
        TimeSpan? lifetime = null)
    {
        _scan = scan;
        _lifetime = lifetime ?? TimeSpan.FromSeconds(30);
    }

    public async Task<IReadOnlyList<PackagedContextMenuDefinition>> GetAsync(
        string userSid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CacheEntry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(userSid, out entry!)
                || entry.Task.IsFaulted
                || entry.Task.IsCanceled
                || entry.Task.IsCompletedSuccessfully
                   && DateTimeOffset.UtcNow - entry.CompletedAtUtc >= _lifetime)
            {
                entry = new CacheEntry();
                _entries[userSid] = entry;
                entry.Task = Task.Run(() =>
                {
                    using var coverage = MenuDiscoveryCoverage.Begin();
                    var result = _scan(userSid);
                    entry.IsComplete = coverage.Complete;
                    entry.CompletedAtUtc = DateTimeOffset.UtcNow;
                    return result;
                });
            }
        }

        // A caller timing out must not cancel discovery used by another snapshot.
        var items = await entry.Task.WaitAsync(cancellationToken);
        if (!entry.IsComplete) MenuDiscoveryCoverage.MarkPackagedIncomplete();
        return items;
    }

    private sealed class CacheEntry
    {
        public Task<IReadOnlyList<PackagedContextMenuDefinition>> Task { get; set; } = null!;

        public DateTimeOffset CompletedAtUtc { get; set; }
        public bool IsComplete { get; set; }
    }
}
