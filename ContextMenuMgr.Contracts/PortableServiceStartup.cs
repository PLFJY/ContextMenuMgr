using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ContextMenuMgr.Contracts;

public enum PortableServiceRegistrationState { Absent, Owned, PendingDeletion, Foreign, Uncertain }

public static class PortableServiceStartup
{
    // Read-only, fixed-service inspection. Each observation closes its SCM
    // handles, so observing pending deletion cannot itself prolong deletion.
    public static PortableServiceRegistrationState Observe(string expectedExecutable)
    {
        var scm = OpenSCManager(null, null, 1);
        if (scm == IntPtr.Zero) return PortableServiceRegistrationState.Uncertain;
        try
        {
            var service = OpenService(scm, ServiceMetadata.ServiceName, 5);
            if (service == IntPtr.Zero) return Marshal.GetLastWin32Error() switch
            {
                1060 => PortableServiceRegistrationState.Absent,
                1072 => PortableServiceRegistrationState.PendingDeletion,
                _ => PortableServiceRegistrationState.Uncertain
            };
            try
            {
                const int bufferSize = 8192;
                var buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    if (!QueryServiceConfig(service, buffer, bufferSize, out _)) return PortableServiceRegistrationState.Uncertain;
                    var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                    if (!string.Equals(Marshal.PtrToStringUni(config.BinaryPath)?.Trim(),
                        $"\"{Path.GetFullPath(expectedExecutable)}\" --service", StringComparison.OrdinalIgnoreCase))
                        return PortableServiceRegistrationState.Foreign;
                    if (!QueryServiceStatus(service, out var status)) return PortableServiceRegistrationState.Uncertain;
                    return status.State == 3 ? PortableServiceRegistrationState.PendingDeletion : PortableServiceRegistrationState.Owned;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }

    public static async Task<PortableServiceRegistrationState> WaitForSettledAsync(
        Func<PortableServiceRegistrationState> observe, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = observe();
            if (state != PortableServiceRegistrationState.PendingDeletion || clock.Elapsed >= timeout) return state;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp((timeout - clock.Elapsed).TotalMilliseconds, 1, 200)), cancellationToken).ConfigureAwait(false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        public uint Type, Start, Error;
        public IntPtr BinaryPath, Group;
        public uint Tag;
        public IntPtr Dependencies, Account, DisplayName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint Type, State, Controls, ExitCode, ServiceExitCode, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, int access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string name, int access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
