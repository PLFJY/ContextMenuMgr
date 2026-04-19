namespace ContextMenuMgr.Contracts;

/// <summary>
/// Represents the backend Notification.
/// </summary>
public sealed record BackendNotification
{
    /// <summary>
    /// Gets or sets the kind.
    /// </summary>
    public PipeNotificationKind Kind { get; init; }

    /// <summary>
    /// Gets or sets the message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the item.
    /// </summary>
    public ContextMenuEntry? Item { get; init; }
}
