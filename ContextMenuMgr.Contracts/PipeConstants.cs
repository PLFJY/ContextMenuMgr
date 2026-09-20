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
    public const string RegistryProtectionTransitionFailed = "REGISTRY_PROTECTION_TRANSITION_FAILED";
    public const string ProtectedRegistryMutationFailed = "PROTECTED_REGISTRY_MUTATION_FAILED";
    public const string RegistrySecurityRestoreFailed = "REGISTRY_SECURITY_RESTORE_FAILED";
    public const string RegistryMutationVerificationFailed = "REGISTRY_MUTATION_VERIFICATION_FAILED";
    public const string RegistryMutationRolledBack = "REGISTRY_MUTATION_ROLLED_BACK";
    public const string RegistryMutationRollbackConflict = "REGISTRY_MUTATION_ROLLBACK_CONFLICT";
    public const string FileTypeActivationVerbProtected = "FILE_TYPE_ACTIVATION_VERB_PROTECTED";
    public const string ShellVerbVisibilityProvenanceMissing = "SHELL_VERB_VISIBILITY_PROVENANCE_MISSING";
}
