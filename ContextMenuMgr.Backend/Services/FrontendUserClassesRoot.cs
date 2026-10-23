using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ContextMenuMgr.Backend.Services;

internal enum ShellNewPhysicalSource
{
    Unresolved,
    User,
    Machine
}

/// <summary>
/// Maps the effective registration observed through HKCR to a writable physical
/// source. The input facts are obtained from Windows' merged view plus explicit
/// physical probes; this type does not implement HKCR merging itself.
/// </summary>
internal static class ShellNewPhysicalSourceResolver
{
    public static ShellNewPhysicalSource Resolve(bool effectiveRegistrationExists, bool userRegistrationExists, bool machineRegistrationExists)
    {
        if (!effectiveRegistrationExists)
        {
            return ShellNewPhysicalSource.Unresolved;
        }

        return userRegistrationExists
            ? ShellNewPhysicalSource.User
            : machineRegistrationExists
                ? ShellNewPhysicalSource.Machine
                : ShellNewPhysicalSource.Unresolved;
    }
}

/// <summary>
/// Opens the interactive frontend user's effective HKCR view for read-only
/// discovery.  This is deliberately separate from the explicit HKU/HKLM
/// roots used by ShellNew mutations.
/// </summary>
internal sealed class FrontendUserClassesRoot : IDisposable
{
    private const int KeyRead = 0x20019;
    private const int KeyWow64_64Key = 0x0100;
    private readonly SafeNativeHandle _token;

    private FrontendUserClassesRoot(SafeNativeHandle token, RegistryKey classesRoot)
    {
        _token = token;
        ClassesRoot = classesRoot;
    }

    public RegistryKey ClassesRoot { get; }

    public static bool TryOpen(BackendUserContext context, FileLogger logger, out FrontendUserClassesRoot? result, out string? failure)
    {
        FrontendUserClassesRoot? opened = null;
        string? error = null;
        using var incoming = WindowsIdentity.GetCurrent();
        using var thread = WindowsIdentity.GetCurrent(ifImpersonating: true);
        var incomingContext = $"ExpectedSid={context.Sid}, ExpectedSession={context.SessionId}, EffectiveSid={incoming.User?.Value}, ThreadImpersonating={thread is not null}, ThreadSid={thread?.User?.Value}, EffectivePrivileges={DescribePrivileges(incoming.Token)}.";
        logger.LogFireAndForget($"ShellNewTokenContext: {incomingContext}");
        // WTSQueryUserToken requires the service identity (LocalSystem + SeTcb),
        // never a pipe caller impersonation. This synchronous scope restores the
        // incoming identity and does not adjust any process or token privileges.
        WindowsIdentity.RunImpersonated(SafeAccessTokenHandle.InvalidHandle, () =>
        {
            using var processIdentity = WindowsIdentity.GetCurrent();
            var processContext = $"ProcessSid={processIdentity.User?.Value}, IsSystem={processIdentity.IsSystem}, RequiredPrivilege=SeTcbPrivilege, Privileges={DescribePrivileges(processIdentity.Token)}.";
            logger.LogFireAndForget($"ShellNewServiceTokenContext: {processContext}");
            var stage = "SessionValidation";
            var stageTimer = System.Diagnostics.Stopwatch.StartNew();
            SafeNativeHandle? token = null;
            try
            {
                if (context.SessionId is not int sessionId || sessionId <= 0)
                    throw new System.ComponentModel.Win32Exception(87, "An interactive frontend SessionId is required.");
                stage = "WTSQueryUserToken";
                if (!NativeMethods.WTSQueryUserToken(sessionId, out var tokenHandle))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                logger.LogFireAndForget($"ShellNewApiTiming: Api=WTSQueryUserToken, Win32Error=0, ElapsedMs={stageTimer.ElapsedMilliseconds}.");
                token = new SafeNativeHandle(tokenHandle);
                using var identity = new WindowsIdentity(tokenHandle);
                stage = "TokenValidation";
                if (!NativeMethods.GetTokenInformation(tokenHandle, 12 /* TokenSessionId */, out var tokenSession, sizeof(int), out _))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                logger.LogFireAndForget($"ShellNewUserToken: TokenSid={identity.User?.Value}, ExpectedSid={context.Sid}, TokenSession={tokenSession}, ExpectedSession={sessionId}, Privileges={DescribePrivileges(tokenHandle)}.");
                if (!string.Equals(identity.User?.Value, context.Sid, StringComparison.OrdinalIgnoreCase) || tokenSession != sessionId)
                    throw new System.ComponentModel.Win32Exception(5, "Frontend SID/session does not match the WTS token.");
                stage = "ProfileAvailability";
                using (var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default))
                using (var hive = users.OpenSubKey(context.Sid, writable: false))
                {
                    var profileAvailable = Directory.Exists(context.ProfilePath);
                    logger.LogFireAndForget($"ShellNewUserProfile: Sid={context.Sid}, ProfilePath={context.ProfilePath}, DirectoryAvailable={profileAvailable}, HiveLoaded={hive is not null}.");
                    if (hive is null || !profileAvailable)
                        throw new System.ComponentModel.Win32Exception(2, "Frontend user profile is not available. No profile will be loaded by discovery.");
                }
                stage = "RegOpenUserClassesRoot";
                stageTimer.Restart();
                var desiredAccess = KeyRead | (Environment.Is64BitOperatingSystem ? KeyWow64_64Key : 0);
                var status = NativeMethods.RegOpenUserClassesRoot(tokenHandle, 0, desiredAccess, out var classesHandle);
                logger.LogFireAndForget($"ShellNewApiTiming: Api=RegOpenUserClassesRoot, Win32Error={status}, ElapsedMs={stageTimer.ElapsedMilliseconds}.");
                if (status != 0) throw new System.ComponentModel.Win32Exception(status);
                var safeClassesHandle = new SafeRegistryHandle(classesHandle, ownsHandle: true);
                try
                {
                    var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
                    opened = new FrontendUserClassesRoot(token, RegistryKey.FromHandle(safeClassesHandle, view));
                    token = null; // ownership transferred with the merged handle
                }
                catch { safeClassesHandle.Dispose(); throw; }
                logger.LogFireAndForget($"ShellNewMergedClassesOpened: Sid={context.Sid}, SessionId={sessionId}, Stage={stage}.");
            }
            catch (Exception ex)
            {
                var code = ex is System.ComponentModel.Win32Exception native ? $"Win32Error={native.NativeErrorCode}" : $"HResult=0x{ex.HResult:X8}";
                error = $"Stage={stage}, ElapsedMs={stageTimer.ElapsedMilliseconds}, {code}, Sid={context.Sid}, SessionId={context.SessionId}, Error={ex.Message}";
                logger.LogFireAndForget(RuntimeLogLevel.Warning, $"ShellNewMergedClassesOpenFailed: {error}. IncomingContext=[{incomingContext}], ProcessContext=[{processContext}].");
            }
            finally { token?.Dispose(); }
        });
        result = opened;
        failure = error;
        return opened is not null;
    }

    private static string DescribePrivileges(IntPtr token)
    {
        _ = NativeMethods.QueryTokenInformation(token, 3 /* TokenPrivileges */, IntPtr.Zero, 0, out var size);
        if (size <= 0) return $"Unavailable({Marshal.GetLastWin32Error()})";
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.QueryTokenInformation(token, 3, buffer, size, out _)) return $"Unavailable({Marshal.GetLastWin32Error()})";
            var count = Marshal.ReadInt32(buffer);
            var privileges = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var entry = IntPtr.Add(buffer, 4 + i * 12);
                var luid = Marshal.ReadInt64(entry);
                var attributes = Marshal.ReadInt32(entry, 8);
                var name = new StringBuilder(128);
                var length = name.Capacity;
                if (NativeMethods.LookupPrivilegeName(null, ref luid, name, ref length))
                    privileges.Add($"{name}:{((attributes & 2) != 0 ? "Enabled" : "Disabled")}");
            }
            return string.Join("|", privileges);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        ClassesRoot.Dispose();
        _token.Dispose();
    }

    private sealed class SafeNativeHandle : SafeHandle
    {
        public SafeNativeHandle(IntPtr handle)
            : base(IntPtr.Zero, ownsHandle: true) => SetHandle(handle);

        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(IntPtr token, int informationClass, out int data, int length, out int required);
        [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
        public static extern bool QueryTokenInformation(IntPtr token, int informationClass, IntPtr data, int length, out int required);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool LookupPrivilegeName(string? system, ref long luid, StringBuilder name, ref int length);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern int RegOpenUserClassesRoot(IntPtr token, int options, int samDesired, out IntPtr result);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
