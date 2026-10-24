using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ContextMenuMgr.Backend.Hosting;

// This identity is constructed exclusively from an authenticated pipe and an
// open process handle. No request-supplied SID, path or session is accepted.
internal sealed class PortableExitCaller : IDisposable
{
    private readonly SafeProcessHandle _process;
    private readonly PortableRuntimeIdentity _files;
    public int ProcessId { get; }
    public int SessionId { get; }
    public string Sid { get; }

    private PortableExitCaller(SafeProcessHandle process, PortableRuntimeIdentity files, int pid, int session, string sid)
        => (_process, _files, ProcessId, SessionId, Sid) = (process, files, pid, session, sid);

    public static PortableExitCaller Authenticate(NamedPipeServerStream pipe, BackendUserContext? user)
    {
        if (RuntimePaths.PackageKind != RuntimePackageKind.Portable || user?.SessionId is not > 0
            || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
            throw new UnauthorizedAccessException("Portable exit requires an authenticated frontend session.");
        var process = OpenProcess(0x101000, false, pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
        var files = new PortableRuntimeIdentity();
        try
        {
            if (process.IsInvalid || !ProcessIdToSessionId(pid, out var session) || session != user.SessionId)
                throw new UnauthorizedAccessException("Pipe client session cannot be verified.");
            if (!OpenProcessToken(process, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Open client token");
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                if (identity.User?.Value != user.Sid) throw new UnauthorizedAccessException("Pipe SID and process token differ.");
            var path = new StringBuilder(32768);
            var size = path.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, path, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Query frontend image");
            var expected = Path.Combine(AppContext.BaseDirectory, "ContextMenuManagerPlus.exe");
            if (!string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Only this package's frontend can request portable exit.");
            files.Pin(expected);
            var caller = new PortableExitCaller(process, files, checked((int)pid), checked((int)session), user.Sid);
            caller.VerifyAlive();
            return caller;
        }
        catch { process.Dispose(); files.Dispose(); throw; }
    }

    public void VerifyAlive()
    {
        if (!GetExitCodeProcess(_process, out var code) || code != 259)
            throw new UnauthorizedAccessException("The authenticated frontend has already exited.");
    }

    public bool OwnsConnectionOrClosed(NamedPipeServerStream pipe)
    {
        // IsConnected alone can stay true until the server reader observes a
        // departed TrayHost. Prove a closed pipe natively instead of treating
        // an unresolvable active client as safe.
        if (!PeekNamedPipe(pipe.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 109 or 232 or 233) return true;
            throw new Win32Exception(error, "Inspect active lifecycle consumers");
        }
        return GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) && pid == ProcessId;
    }

    public void Dispose() { _files.Dispose(); _process.Dispose(); }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(SafePipeHandle pipe, IntPtr buffer, uint size, IntPtr read, IntPtr available, IntPtr remaining);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
