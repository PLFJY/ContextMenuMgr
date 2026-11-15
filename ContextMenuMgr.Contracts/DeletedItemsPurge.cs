namespace ContextMenuMgr.Contracts;

/// <summary>A fixed selection of recovery records, captured before confirmation.</summary>
public sealed record DeletedItemReference(string Id, DateTimeOffset? DeletedAtUtc);

public sealed record DeletedItemsPurgeRequest
{
    public ContextMenuCategory Category { get; init; }
    public IReadOnlyList<DeletedItemReference> Items { get; init; } = [];
}

public sealed record DeletedItemsPurgeResult
{
    public IReadOnlyList<DeletedItemReference> PurgedItems { get; init; } = [];
    public IReadOnlyList<string> SkippedItemIds { get; init; } = [];
    public IReadOnlyList<string> BackupCleanupFailedItemIds { get; init; } = [];
}
