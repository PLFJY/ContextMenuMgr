namespace ContextMenuMgr.Contracts;

public sealed record FrontendControlResponse
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;
}
