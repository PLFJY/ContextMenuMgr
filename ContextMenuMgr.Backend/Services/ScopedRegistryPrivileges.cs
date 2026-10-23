using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using ContextMenuMgr.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ContextMenuMgr.Backend.Services;

// Never adjust the shared process token: overlapping scopes cannot safely restore
// its previous privilege state. Each synchronous mutation owns a disposable copy.
internal static class ScopedRegistryPrivileges
{
    private const uint TokenQuery = 0x8;
    private const uint TokenDuplicate = 0x2;
    private const uint TokenImpersonate = 0x4;
    private const uint TokenAdjustPrivileges = 0x20;
    private const uint PrivilegeEnabled = 0x2;
    private const int ErrorNotAllAssigned = 1300;

    internal static void Run(Action action)
    {
        var incomingContext = DescribeContext();
        // A pipe identity is evidence for selecting the user's physical hive,
        // never the identity used for protected machine writes. Restore any
        // incoming impersonation on exit, including all acquisition failures.
        WindowsIdentity.RunImpersonated(SafeAccessTokenHandle.InvalidHandle, () =>
        {
            var phase = "OpenProcessToken";
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenDuplicate, out var processToken))
                    throw NativeFailure(phase);
                using (processToken)
                {
                    phase = "DuplicateServiceToken";
                    // Do not request ADJUST_PRIVILEGES against the primary token's
                    // DACL, even when obtaining the duplicate. The copy receives its
                    // own default descriptor; open it for adjustment after attaching.
                    if (!DuplicateTokenEx(processToken, TokenQuery | TokenImpersonate, IntPtr.Zero,
                            2 /* SecurityImpersonation */, 2 /* TokenImpersonation */, out var copy))
                        throw NativeFailure(phase);
                    using (copy)
                    {
                        phase = "AttachServiceToken";
                        WindowsIdentity.RunImpersonated(copy, () =>
                        {
                            EnableRequiredPrivileges();
                            action();
                        });
                    }
                }
            }
            catch (Win32Exception ex)
            {
                var detail = ex.Message.Contains("Phase=", StringComparison.Ordinal)
                    ? ex.Message : $"Phase={phase}; {ex.Message}";
                throw new ProtectedRegistryMutationException(PipeErrorCodes.ProtectedRegistryMutationFailed,
                    $"Protected registry privilege scope failed. {detail} Win32Error={ex.NativeErrorCode}; IncomingContext=[{incomingContext}]; ServiceContext=[{DescribeContext()}].", ex);
            }
        });
    }

    private static void EnableRequiredPrivileges()
    {
        if (!OpenThreadToken(GetCurrentThread(), TokenQuery | TokenAdjustPrivileges, true, out var token))
            throw NativeFailure("OpenPrivateThreadToken");
        using (token)
        {
            foreach (var privilege in new[] { "SeTakeOwnershipPrivilege", "SeRestorePrivilege" })
            {
                if (!LookupPrivilegeValue(null, privilege, out var luid))
                    throw NativeFailure($"LookupPrivilege; Privilege={privilege}");
                var state = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = PrivilegeEnabled };
                var adjusted = AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
                var error = Marshal.GetLastWin32Error();
                if (!adjusted || error != 0)
                    throw new Win32Exception(error,
                        $"Phase=EnablePrivilege; Privilege={privilege}; Reason={(error == ErrorNotAllAssigned ? "PrivilegeAbsent" : "EnableFailed")}; Win32Error={error}.");

                var required = new PRIVILEGE_SET { Count = 1, Control = 1, Luid = luid, Attributes = PrivilegeEnabled };
                if (!PrivilegeCheck(token, ref required, out var enabled))
                    throw NativeFailure($"VerifyPrivilege; Privilege={privilege}");
                if (!enabled)
                    throw new Win32Exception(1314,
                        $"Phase=VerifyPrivilege; Privilege={privilege}; Reason=PrivilegeNotEnabled; Win32Error=1314.");
            }
        }
        // On any partial failure the copy is detached and discarded. No privilege
        // restore call can fail or race with another request's process-token state.
    }

    internal static string DescribeContext()
    {
        try
        {
            using var effective = WindowsIdentity.GetCurrent();
            using var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true);
            return $"PID={Environment.ProcessId}; ThreadId={GetCurrentThreadId()}; EffectiveIsSystem={effective.IsSystem}; IsImpersonating={impersonated is not null}; ImpersonationLevel={effective.ImpersonationLevel}";
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return $"PID={Environment.ProcessId}; ContextUnavailable={ex.GetType().Name}; HResult=0x{ex.HResult:X8}";
        }
    }

    private static Win32Exception NativeFailure(string phase)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"Phase={phase}; Win32Error={error}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PRIVILEGE_SET { public uint Count; public uint Control; public LUID Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle copy);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenThreadToken(IntPtr thread, uint access, bool openAsSelf, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TOKEN_PRIVILEGES state, uint size, IntPtr previous, IntPtr length);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool PrivilegeCheck(SafeAccessTokenHandle token, ref PRIVILEGE_SET required, [MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
