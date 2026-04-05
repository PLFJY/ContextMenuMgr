namespace ContextMenuMgr.Frontend.Services;

public sealed record BackendServiceBootstrapResult(
    bool Success,
    bool Cancelled,
    string Code,
    string Detail);
