namespace ContextMenuMgr.Contracts;

/// <summary>
/// Represents the pipe Response.
/// </summary>
public sealed record PipeResponse
{
    /// <summary>
    /// Gets or sets the success.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets or sets the message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the items.
    /// </summary>
    public IReadOnlyList<ContextMenuEntry> Items { get; init; } = [];

    /// <summary>
    /// Gets or sets the item.
    /// </summary>
    public ContextMenuEntry? Item { get; init; }

    /// <summary>
    /// Gets or sets the registry Protection Enabled.
    /// </summary>
    public bool? RegistryProtectionEnabled { get; init; }
}
