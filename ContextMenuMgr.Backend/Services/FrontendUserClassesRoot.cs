using System.Runtime.InteropServices;
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

    public static bool TryOpen(BackendUserContext context, out FrontendUserClassesRoot? result, out int win32Error)
    {
        result = null;
        win32Error = 0;
        if (context.SessionId is not int sessionId || sessionId < 0)
        {
            win32Error = 87; // ERROR_INVALID_PARAMETER
            return false;
        }

        if (!NativeMethods.WTSQueryUserToken(sessionId, out var tokenHandle))
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        var token = new SafeNativeHandle(tokenHandle);
        var desiredAccess = KeyRead | (Environment.Is64BitOperatingSystem ? KeyWow64_64Key : 0);
        var status = NativeMethods.RegOpenUserClassesRoot(token.DangerousGetHandle(), 0, desiredAccess, out var classesHandle);
        if (status != 0)
        {
            token.Dispose();
            win32Error = status;
            return false;
        }

        var safeClassesHandle = new SafeRegistryHandle(classesHandle, ownsHandle: true);
        try
        {
            var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            result = new FrontendUserClassesRoot(token, RegistryKey.FromHandle(safeClassesHandle, view));
            return true;
        }
        catch
        {
            safeClassesHandle.Dispose();
            token.Dispose();
            throw;
        }
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
