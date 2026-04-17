namespace ContextMenuMgr.Contracts;

public sealed record FrontendControlRequest
{
    public FrontendControlCommand Command { get; init; }

    public string? FocusItemId { get; init; }
}
