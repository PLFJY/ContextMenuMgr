namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the backend Service Bootstrap Result.
/// </summary>
public sealed record BackendServiceBootstrapResult(
    bool Success,
    bool Cancelled,
    string Code,
    string Detail);
