namespace ContextMenuMgr.Frontend.Services;

public sealed class BackendRequestException : InvalidOperationException
{
    public BackendRequestException(
        string message,
        string? errorCode,
        bool? registryProtectionEnabled = null)
        : base(message)
    {
        ErrorCode = errorCode;
        RegistryProtectionEnabled = registryProtectionEnabled;
    }

    public string? ErrorCode { get; }

    public bool? RegistryProtectionEnabled { get; }
}
