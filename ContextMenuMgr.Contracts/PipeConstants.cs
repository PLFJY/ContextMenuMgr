namespace ContextMenuMgr.Contracts;

/// <summary>
/// Represents the pipe Constants.
/// </summary>
public static class PipeConstants
{
    public const string PipeName = "ContextMenuMgr.Backend";

    public const string FrontendControlPipeName = "ContextMenuMgr.Frontend.Control";

    public const string TrayHostControlPipeName = "ContextMenuMgr.TrayHost.Control";
}

public static class PipeErrorCodes
{
    public const string RegistryWriteProtectionEnabled = "REGISTRY_WRITE_PROTECTION_ENABLED";
    public const string ProtectedRegistryMutationFailed = "PROTECTED_REGISTRY_MUTATION_FAILED";
    public const string RegistrySecurityRestoreFailed = "REGISTRY_SECURITY_RESTORE_FAILED";
    public const string RegistryMutationVerificationFailed = "REGISTRY_MUTATION_VERIFICATION_FAILED";
}
