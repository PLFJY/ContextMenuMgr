namespace ContextMenuMgr.Contracts;

/// <summary>
/// Represents the frontend Control Request.
/// </summary>
public sealed record FrontendControlRequest
{
    /// <summary>
    /// Gets or sets the command.
    /// </summary>
    public FrontendControlCommand Command { get; init; }

    /// <summary>
    /// Gets or sets the focus Item Id.
    /// </summary>
    public string? FocusItemId { get; init; }
}
