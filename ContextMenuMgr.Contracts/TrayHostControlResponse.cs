namespace ContextMenuMgr.Contracts;

/// <summary>
/// Represents the tray Host Control Response.
/// </summary>
public sealed record TrayHostControlResponse
{
    /// <summary>
    /// Gets or sets the success.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets or sets the message.
    /// </summary>
    public string Message { get; init; } = string.Empty;
}
