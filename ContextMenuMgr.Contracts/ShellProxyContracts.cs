namespace ContextMenuMgr.Contracts;

/// <summary>Describes the persistent in-process proxy that owns a shell-extension registration.</summary>
public sealed record ShellProxyWrapperRequest
{
    public ContextMenuEntry? Item { get; init; }
    public string? MenuTitle { get; init; }
}

/// <summary>Persistent wrapper state returned by the backend.</summary>
public sealed record ShellProxyWrapperStatus
{
    public bool IsWrapped { get; init; }
    public string? ProxyClsid { get; init; }
    public string? OriginalHandlerClsid { get; init; }
    public string? MenuTitle { get; init; }
    public string? Health { get; init; }
    public string? Message { get; init; }
}
