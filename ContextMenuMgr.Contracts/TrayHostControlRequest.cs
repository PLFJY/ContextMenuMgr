namespace ContextMenuMgr.Contracts;

public sealed record TrayHostControlRequest
{
    public TrayHostControlCommand Command { get; init; }
}
