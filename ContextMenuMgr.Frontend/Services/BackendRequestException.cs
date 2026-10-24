namespace ContextMenuMgr.Frontend.Services;

public sealed class BackendRequestException : InvalidOperationException
{
    public BackendRequestException(
        string message,
        string? errorCode,
        bool? registryProtectionEnabled = null,
        IReadOnlyList<string>? removedItemIds = null)
        : base(message)
    {
        ErrorCode = errorCode;
        RegistryProtectionEnabled = registryProtectionEnabled;
        RemovedItemIds = removedItemIds ?? [];
    }

    public string? ErrorCode { get; }

    public bool? RegistryProtectionEnabled { get; }

    public IReadOnlyList<string> RemovedItemIds { get; }
}
