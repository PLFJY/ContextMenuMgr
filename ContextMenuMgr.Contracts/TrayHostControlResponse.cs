namespace ContextMenuMgr.Contracts;

public sealed record TrayHostControlResponse
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;
}
