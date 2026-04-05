namespace ContextMenuMgr.Contracts;

public sealed record BackendNotification
{
    public PipeNotificationKind Kind { get; init; }

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public ContextMenuEntry? Item { get; init; }
}
