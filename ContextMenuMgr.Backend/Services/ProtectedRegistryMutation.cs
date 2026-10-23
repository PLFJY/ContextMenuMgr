using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Performs a single value-level mutation on a protected machine Classes key.
/// The ordinary registry write path must always be attempted before this helper.
/// </summary>
internal static class ProtectedRegistryMutation
{
    private const string MachineClassesPrefix = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\";
    private const string ShortMachineClassesPrefix = @"HKLM\SOFTWARE\Classes\";
    private static readonly SecurityIdentifier LocalSystemSid = new(WellKnownSidType.LocalSystemSid, null);

    internal static bool IsEligibleMachineClassesPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !(path.StartsWith(MachineClassesPrefix, StringComparison.OrdinalIgnoreCase)
                 || path.StartsWith(ShortMachineClassesPrefix, StringComparison.OrdinalIgnoreCase))) return false;
        var parts = GetMachineClassesSubPath(path).Split('\\');
        return parts.Length >= 3 && parts[^2].Equals("shell", StringComparison.OrdinalIgnoreCase)
            && parts.All(part => part.Length > 0 && part is not "." and not ".." && !part.Contains('/'));
    }

    internal static void Execute(
        string absoluteRegistryPath,
        Action<RegistryKey> mutation,
        Action<RegistryKey> verify)
    {
        if (!IsEligibleMachineClassesPath(absoluteRegistryPath))
        {
            throw new ProtectedRegistryMutationException(
                PipeErrorCodes.ProtectedRegistryMutationFailed,
                "Protected registry fallback is only permitted for physical machine Classes ShellVerb keys.");
        }

        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(verify);

        ScopedRegistryPrivileges.Run(() => ExecuteWithPrivileges(absoluteRegistryPath, mutation, verify));
    }

    private static void ExecuteWithPrivileges(string absoluteRegistryPath, Action<RegistryKey> mutation, Action<RegistryKey> verify)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem)
        {
            throw new ProtectedRegistryMutationException(PipeErrorCodes.ProtectedRegistryMutationFailed,
                "Phase=ServiceIdentity; protected registry mutation requires the LocalSystem backend service.");
        }

        var subPath = GetMachineClassesSubPath(absoluteRegistryPath);
        byte[] originalDescriptor;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Classes\{subPath}",
                RegistryKeyPermissionCheck.ReadSubTree,
                RegistryRights.ReadPermissions)
                ?? throw new InvalidOperationException($"Registry key was not found: {absoluteRegistryPath}");
            originalDescriptor = key.GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            throw new ProtectedRegistryMutationException(
                PipeErrorCodes.ProtectedRegistryMutationFailed,
                $"Phase=ReadRegistrySecurity; unable to capture the original registry security. HResult=0x{ex.HResult:X8}; {ScopedRegistryPrivileges.DescribeContext()}",
                ex);
        }

        var securityChanged = false;
        byte[]? ownedDescriptor = null;
        byte[]? temporaryDescriptor = null;
        var phase = "TakeOwnership";
        Exception? mutationFailure = null;
        try
        {
            // SYSTEM has no SetValue or ChangePermissions right on several Windows
            // shell verbs. Taking ownership is the minimum escalation that permits
            // a temporary value-only ACE; the original owner is restored below.
            using (var ownershipKey = OpenWithRights(
                       subPath,
                       RegistryRights.ReadPermissions | RegistryRights.TakeOwnership))
            {
                var ownershipSecurity = ownershipKey.GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
                RequireDescriptor(ownershipSecurity, originalDescriptor);
                ownershipSecurity.SetOwner(LocalSystemSid);
                ownedDescriptor = ownershipSecurity.GetSecurityDescriptorBinaryForm();
                // SetAccessControl can fail after a partial native update.
                securityChanged = true;
                ownershipKey.SetAccessControl(ownershipSecurity);
            }

            phase = "GrantValueAccess";
            using (var accessKey = OpenWithRights(
                       subPath,
                       RegistryRights.ReadPermissions | RegistryRights.ChangePermissions))
            {
                var temporarySecurity = accessKey.GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
                RequireDescriptor(temporarySecurity, ownedDescriptor!);
                EnsureNoExplicitSystemSetValueDeny(temporarySecurity);
                temporarySecurity.AddAccessRule(new RegistryAccessRule(
                    LocalSystemSid,
                    RegistryRights.SetValue,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                temporaryDescriptor = temporarySecurity.GetSecurityDescriptorBinaryForm();
                accessKey.SetAccessControl(temporarySecurity);
            }

            phase = "RegistryMutation";
            using var writableKey = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Classes\{subPath}",
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ReadKey | RegistryRights.SetValue)
                ?? throw new InvalidOperationException($"Registry key was not found: {absoluteRegistryPath}");
            mutation(writableKey);
            phase = "RegistryVerification";
            verify(writableKey);
        }
        catch (Exception ex)
        {
            mutationFailure = ex;
        }
        finally
        {
            if (securityChanged)
            {
                try
                {
                    RestoreDescriptor(subPath, originalDescriptor, ownedDescriptor, temporaryDescriptor);
                    VerifyDescriptor(subPath, originalDescriptor);
                }
                catch (Exception restoreFailure)
                {
                    throw new ProtectedRegistryMutationException(
                        PipeErrorCodes.RegistrySecurityRestoreFailed,
                        $"Phase=RegistrySecurityRestore; registry security restoration failed. HResult=0x{restoreFailure.HResult:X8}; {ScopedRegistryPrivileges.DescribeContext()}",
                        mutationFailure is null ? restoreFailure : new AggregateException(mutationFailure, restoreFailure));
                }
            }
        }

        if (mutationFailure is not null)
        {
            throw new ProtectedRegistryMutationException(
                mutationFailure is ProtectedRegistryMutationException protectedFailure ? protectedFailure.ErrorCode
                    : mutationFailure is ShellVerbMutationException transactionFailure ? transactionFailure.ErrorCode
                    : PipeErrorCodes.ProtectedRegistryMutationFailed,
                $"Phase={phase}; protected registry mutation failed. HResult=0x{mutationFailure.HResult:X8}; {ScopedRegistryPrivileges.DescribeContext()}",
                mutationFailure);
        }
    }

    private static RegistryKey OpenWithRights(string subPath, RegistryRights rights)
        => Registry.LocalMachine.OpenSubKey(
               $@"SOFTWARE\Classes\{subPath}",
               RegistryKeyPermissionCheck.ReadWriteSubTree,
               rights)
           ?? throw new InvalidOperationException($"Registry key was not found: HKLM\\SOFTWARE\\Classes\\{subPath}");

    private static void RestoreDescriptor(string subPath, byte[] originalDescriptor, byte[]? ownedDescriptor, byte[]? temporaryDescriptor)
    {
        using var observed = OpenWithRights(subPath, RegistryRights.ReadPermissions);
        var current = observed.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access)
            .GetSecurityDescriptorBinaryForm();
        if (current.AsSpan().SequenceEqual(originalDescriptor)) return;
        if (!current.AsSpan().SequenceEqual(ownedDescriptor) && !current.AsSpan().SequenceEqual(temporaryDescriptor))
            throw new SecurityException("Registry security changed concurrently; the newer descriptor was not overwritten.");
        using var key = OpenWithRights(subPath, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership);
        RequireDescriptor(key.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access), current);
        var security = new RegistrySecurity();
        // Only these sections were captured. All would mark the uncaptured SACL
        // dirty as well, requesting audit changes outside this fallback's scope.
        security.SetSecurityDescriptorBinaryForm(originalDescriptor,
            AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
        key.SetAccessControl(security);
    }

    private static void RequireDescriptor(RegistrySecurity security, byte[] expected)
    {
        if (!security.GetSecurityDescriptorBinaryForm().AsSpan().SequenceEqual(expected))
            throw new SecurityException("Registry security changed concurrently; temporary permissions were not applied.");
    }

    private static void VerifyDescriptor(string subPath, byte[] originalDescriptor)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Classes\{subPath}",
            RegistryKeyPermissionCheck.ReadSubTree,
            RegistryRights.ReadPermissions)
            ?? throw new InvalidOperationException($"Registry key was not found while verifying security restoration: HKLM\\SOFTWARE\\Classes\\{subPath}");
        var restoredDescriptor = key.GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        if (!originalDescriptor.AsSpan().SequenceEqual(restoredDescriptor))
        {
            throw new SecurityException("The registry security descriptor did not match its pre-mutation value after restoration.");
        }
    }

    private static void EnsureNoExplicitSystemSetValueDeny(RegistrySecurity security)
    {
        foreach (RegistryAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Deny
                && rule.IdentityReference is SecurityIdentifier sid
                && sid.Equals(LocalSystemSid)
                && (rule.RegistryRights & RegistryRights.SetValue) != 0)
            {
                throw new ProtectedRegistryMutationException(
                    PipeErrorCodes.ProtectedRegistryMutationFailed,
                    "The protected registry entry explicitly denies SYSTEM value writes and cannot be safely changed.");
            }
        }
    }

    private static string GetMachineClassesSubPath(string path)
    {
        if (path.StartsWith(MachineClassesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path[MachineClassesPrefix.Length..];
        }

        return path[ShortMachineClassesPrefix.Length..];
    }
}

internal sealed class ProtectedRegistryMutationException : Exception
{
    public ProtectedRegistryMutationException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
