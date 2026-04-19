namespace ContextMenuMgr.Contracts;

/// <summary>
/// Represents the tray Host Control Request.
/// </summary>
public sealed record TrayHostControlRequest
{
    /// <summary>
    /// Gets or sets the command.
    /// </summary>
    public TrayHostControlCommand Command { get; init; }
}
