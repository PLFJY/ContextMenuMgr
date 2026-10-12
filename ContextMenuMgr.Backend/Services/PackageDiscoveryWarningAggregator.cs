namespace ContextMenuMgr.Backend.Services;

/// <summary>Bounded per-user/stage suppression; changed conditions are never hidden.</summary>
internal sealed class PackageDiscoveryWarningAggregator(TimeSpan interval, int capacity)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Observe(string scope, string? signature, string? detail, DateTimeOffset now)
    {
        lock (_gate)
        {
            var messages = new List<string>(3);
            if (_entries.TryGetValue(scope, out var previous))
            {
                if (signature == previous.Signature)
                {
                    previous.Suppressed = Math.Min(long.MaxValue - 1, previous.Suppressed) + 1;
                    previous.LastSeen = now;
                    if (now < previous.LastEmitted || now - previous.LastEmitted >= interval)
                    {
                        messages.Add(Summary(previous, "periodic"));
                        previous.Suppressed = 0;
                        previous.LastEmitted = now;
                    }
                    return messages;
                }
                if (previous.Suppressed > 0) messages.Add(Summary(previous, signature is null ? "resolved" : "changed"));
                _entries.Remove(scope);
            }
            if (signature is null || detail is null) return messages;
            if (_entries.Count >= capacity)
            {
                var oldest = _entries.MinBy(pair => pair.Value.LastSeen);
                if (oldest.Value.Suppressed > 0) messages.Add(Summary(oldest.Value, "evicted"));
                _entries.Remove(oldest.Key);
            }
            // Only bounded diagnostic samples are retained, never package lists or exception objects.
            detail = detail.Length > 8192 ? detail[..8173] + " [SamplesTruncated]" : detail;
            _entries[scope] = new Entry(signature, detail, now);
            messages.Add(detail);
            return messages;
        }
    }

    private static string Summary(Entry entry, string reason)
        => $"PackageDiscoveryWarningSummary: Reason={reason}, SuppressedOccurrences={entry.Suppressed}, Condition=[{entry.Detail}]";

    private sealed class Entry(string signature, string detail, DateTimeOffset now)
    {
        public string Signature { get; } = signature;
        public string Detail { get; } = detail;
        public DateTimeOffset LastEmitted { get; set; } = now;
        public DateTimeOffset LastSeen { get; set; } = now;
        public long Suppressed { get; set; }
    }
}
