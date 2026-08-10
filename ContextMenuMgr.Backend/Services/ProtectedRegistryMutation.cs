using System.ComponentModel;
using System.Runtime.InteropServices;
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
        => !string.IsNullOrWhiteSpace(path)
           && (path.StartsWith(MachineClassesPrefix, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(ShortMachineClassesPrefix, StringComparison.OrdinalIgnoreCase));

    internal static void Execute(
        string absoluteRegistryPath,
        Action<RegistryKey> mutation,
        Action<RegistryKey> verify)
    {
        if (!IsEligibleMachineClassesPath(absoluteRegistryPath))
        {
            throw new ProtectedRegistryMutationException(
                PipeErrorCodes.ProtectedRegistryMutationFailed,
                "Protected registry fallback is only permitted for machine Classes keys.");
        }

        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(verify);

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
                "Windows denied access while reading the protected registry entry.",
                ex);
        }

        ScopedTokenPrivilege takeOwnership;
        ScopedTokenPrivilege restore;
        try
        {
            takeOwnership = ScopedTokenPrivilege.Enable("SeTakeOwnershipPrivilege");
            try
            {
                restore = ScopedTokenPrivilege.Enable("SeRestorePrivilege");
            }
            catch
            {
                takeOwnership.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is PrivilegeNotHeldException or Win32Exception)
        {
            throw new ProtectedRegistryMutationException(
                PipeErrorCodes.ProtectedRegistryMutationFailed,
                "Windows did not grant the service the privilege needed to update the protected registry entry.",
                ex);
        }

        using (takeOwnership)
        using (restore)
        {
            var securityChanged = false;
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
                    ownershipSecurity.SetOwner(LocalSystemSid);
                    ownershipKey.SetAccessControl(ownershipSecurity);
                    securityChanged = true;
                }

                using (var accessKey = OpenWithRights(
                           subPath,
                           RegistryRights.ReadPermissions | RegistryRights.ChangePermissions))
                {
                    var temporarySecurity = accessKey.GetAccessControl(
                        AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);
                    EnsureNoExplicitSystemSetValueDeny(temporarySecurity);
                    temporarySecurity.AddAccessRule(new RegistryAccessRule(
                        LocalSystemSid,
                        RegistryRights.SetValue,
                        InheritanceFlags.None,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                    accessKey.SetAccessControl(temporarySecurity);
                }

                using var writableKey = Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Classes\{subPath}",
                    RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryRights.ReadKey | RegistryRights.SetValue)
                    ?? throw new InvalidOperationException($"Registry key was not found: {absoluteRegistryPath}");
                mutation(writableKey);
                verify(writableKey);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or PrivilegeNotHeldException)
            {
                mutationFailure = ex;
            }
            finally
            {
                if (securityChanged)
                {
                    try
                    {
                        RestoreDescriptor(subPath, originalDescriptor);
                        VerifyDescriptor(subPath, originalDescriptor);
                    }
                    catch (Exception restoreFailure)
                    {
                        throw new ProtectedRegistryMutationException(
                            PipeErrorCodes.RegistrySecurityRestoreFailed,
                            "Registry security restoration failed after the requested visibility change. The operation was not reported as successful.",
                            restoreFailure);
                    }
                }
            }

            if (mutationFailure is not null)
            {
                throw new ProtectedRegistryMutationException(
                    PipeErrorCodes.ProtectedRegistryMutationFailed,
                    "Windows denied access to the protected registry entry. No successful visibility change was verified.",
                    mutationFailure);
            }
        }
    }

    private static RegistryKey OpenWithRights(string subPath, RegistryRights rights)
        => Registry.LocalMachine.OpenSubKey(
               $@"SOFTWARE\Classes\{subPath}",
               RegistryKeyPermissionCheck.ReadWriteSubTree,
               rights)
           ?? throw new InvalidOperationException($"Registry key was not found: HKLM\\SOFTWARE\\Classes\\{subPath}");

    private static void RestoreDescriptor(string subPath, byte[] originalDescriptor)
    {
        using var key = OpenWithRights(subPath, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership);
        var security = new RegistrySecurity();
        security.SetSecurityDescriptorBinaryForm(originalDescriptor);
        key.SetAccessControl(security);
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

internal sealed class ScopedTokenPrivilege : IDisposable
{
    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x8;
    private const uint SePrivilegeEnabled = 0x2;
    private const int ErrorNotAllAssigned = 1300;
    private IntPtr _token;
    private TOKEN_PRIVILEGES _previous;
    private bool _restore;

    private ScopedTokenPrivilege()
    {
    }

    public static ScopedTokenPrivilege Enable(string privilegeName)
    {
        var result = new ScopedTokenPrivilege();
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out result._token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open the service process token.");
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to resolve {privilegeName}.");
            }

            var enabled = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Privilege = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SePrivilegeEnabled } };
            var size = (uint)Marshal.SizeOf<TOKEN_PRIVILEGES>();
            if (!AdjustTokenPrivileges(result._token, false, ref enabled, size, out result._previous, out _)
                || Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
            {
                throw new PrivilegeNotHeldException(privilegeName);
            }

            result._restore = true;
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_token == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (_restore)
            {
                var size = (uint)Marshal.SizeOf<TOKEN_PRIVILEGES>();
                if (!AdjustTokenPrivileges(_token, false, ref _previous, size, out _, out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to restore the service token privilege state.");
                }
            }
        }
        finally
        {
            CloseHandle(_token);
            _token = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState, uint bufferLength, out TOKEN_PRIVILEGES previousState, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
