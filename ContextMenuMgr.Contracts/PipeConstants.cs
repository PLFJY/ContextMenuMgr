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
    public const string BackendCapabilityUnavailable = "BACKEND_CAPABILITY_UNAVAILABLE";
    public const string InteractiveUserUnavailable = "INTERACTIVE_USER_UNAVAILABLE";
    public const string MenuItemRemoved = "MENU_ITEM_REMOVED";
    public const string RegistryWriteProtectionEnabled = "REGISTRY_WRITE_PROTECTION_ENABLED";
    public const string RegistryProtectionTransitionFailed = "REGISTRY_PROTECTION_TRANSITION_FAILED";
    public const string ProtectedRegistryMutationFailed = "PROTECTED_REGISTRY_MUTATION_FAILED";
    public const string RegistrySecurityRestoreFailed = "REGISTRY_SECURITY_RESTORE_FAILED";
    public const string RegistryMutationVerificationFailed = "REGISTRY_MUTATION_VERIFICATION_FAILED";
    public const string RegistryMutationRolledBack = "REGISTRY_MUTATION_ROLLED_BACK";
    public const string RegistryMutationRollbackConflict = "REGISTRY_MUTATION_ROLLBACK_CONFLICT";
    public const string FileTypeActivationVerbProtected = "FILE_TYPE_ACTIVATION_VERB_PROTECTED";
    public const string ShellVerbVisibilityProvenanceMissing = "SHELL_VERB_VISIBILITY_PROVENANCE_MISSING";
    public const string DocumentIconRepairSourceUnavailable = "DOCUMENT_ICON_REPAIR_SOURCE_UNAVAILABLE";
    public const string DocumentIconRepairConflict = "DOCUMENT_ICON_REPAIR_CONFLICT";
    public const string DocumentIconRepairRecoveryInvalid = "DOCUMENT_ICON_REPAIR_RECOVERY_INVALID";
    public const string DocumentIconRepairUnavailable = "DOCUMENT_ICON_REPAIR_UNAVAILABLE";
    public const string DocumentIconRepairAccessDenied = "DOCUMENT_ICON_REPAIR_ACCESS_DENIED";
    public const string DocumentIconRepairFailed = "DOCUMENT_ICON_REPAIR_FAILED";
    public const string DocumentIconRepairMustRestore = "DOCUMENT_ICON_REPAIR_MUST_RESTORE";
}
