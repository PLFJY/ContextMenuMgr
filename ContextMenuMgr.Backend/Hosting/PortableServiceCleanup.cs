using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using static ContextMenuMgr.Backend.Hosting.ServiceScmNative;

namespace ContextMenuMgr.Backend.Hosting;

internal sealed record PortableCleanupResult(string Stage, string Code, int Win32Error = 0)
{
    public static PortableCleanupResult FromWin32(string stage, int error) => new(stage, error switch
    {
        0 => "MarkedForDeletion", 1072 => "AlreadyMarkedForDeletion", 1060 => "Absent",
        5 => "AccessDenied", 1115 => "ShutdownInProgress", 1722 => "ScmUnavailable",
        _ => "NativeFailure"
    }, error);
}

internal static class PortableServiceConsumers
{
    public static bool IsExclusiveFrontend(int callerPid, int callerSession,
        IReadOnlyCollection<int> userSessions, IReadOnlyCollection<int> consumerPids)
        => callerPid > 0 && callerSession > 0
           && userSessions.Contains(callerSession)
           && userSessions.All(session => session == callerSession)
           && consumerPids.All(pid => pid == callerPid);
}

internal static class PortableServiceCleanup
{
    public static void VerifyFrontendCanStop(PortableExitCaller caller)
    {
        caller.VerifyAlive();
        VerifyConsumers(windowsShutdown: false, caller);
    }

    public static void Attempt(bool windowsShutdown, PortableExitCaller? caller)
    {
        var stage = "Package";
        PortableCleanupResult result;
        try
        {
            if (RuntimePaths.PackageKind != RuntimePackageKind.Portable) return;
            stage = "ProcessIdentity";
            using var threadIdentity = WindowsIdentity.GetCurrent(ifImpersonating: true);
            using var identity = WindowsIdentity.GetCurrent();
            using var currentProcess = Process.GetCurrentProcess();
            if (threadIdentity is not null || identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true
                || currentProcess.SessionId != 0
                || !Environment.GetCommandLineArgs().Skip(1).SequenceEqual(["--service"], StringComparer.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Self-removal requires the non-impersonated service process.");
            using var lifecycle = ServiceLifecycleLease.Acquire(TimeSpan.Zero);
            using var files = new PortableRuntimeIdentity();
            stage = "RuntimeOwnership";
            var executable = Environment.ProcessPath ?? throw new IOException("Backend image unavailable.");
            if (!string.Equals(Path.GetDirectoryName(executable)?.TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Backend is outside this runtime package.");
            files.Pin(executable);
            files.Pin(Path.Combine(AppContext.BaseDirectory, RuntimePackageManifest.FileName));
            if (RuntimePackageManifest.Read(AppContext.BaseDirectory).PackageKind != RuntimePackageKind.Portable)
                throw new IOException("Explicit Portable manifest cannot be verified.");
            stage = "PersistedPolicy";
            if (PortableServiceExitPolicy.HasUnsettledStartupTransaction(RuntimePaths.DataDirectory))
                throw new IOException("A Portable startup transaction is incomplete; explicit repair is required.");
            files.Pin(RuntimePaths.SettingsPath);
            PortableServiceExitPolicy policy;
            using (var settings = File.OpenRead(RuntimePaths.SettingsPath)) policy = PortableServiceExitPolicy.Read(settings);
            if (!policy.AllowsRemoval(RuntimePaths.PackageKind, windowsShutdown))
            {
                Log(new(stage, "RetainedByPolicy"), windowsShutdown, caller);
                return;
            }
            stage = "UserPoliciesAndConsumers";
            VerifyConsumers(windowsShutdown, caller);
            VerifyUserPolicies(windowsShutdown, caller);
            stage = "OpenSCManager";
            using var scm = new SafeScHandle(OpenSCManager(null, null, 1));
            if (scm.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            stage = "OpenOwnService";
            // QUERY_CONFIG | QUERY_STATUS | DELETE; no change-config or stop rights.
            using var service = new SafeScHandle(OpenService(scm.DangerousGetHandle(), ServiceMetadata.ServiceName, 0x10005));
            if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            stage = "ScmOwnership";
            var registration = ReadRunningRegistration(service);
            if (!OwnsRegistration(registration.ImagePath, executable, registration.ProcessId, Environment.ProcessId)
                || registration.StartType != 3)
                throw new UnauthorizedAccessException("SCM image, PID or demand-start ownership mismatch.");
            // Recheck immediately before mutation, on the same open SCM object.
            caller?.VerifyAlive();
            VerifyConsumers(windowsShutdown, caller);
            VerifyUserPolicies(windowsShutdown, caller);
            if (ReadRunningRegistration(service) != registration)
                throw new IOException("Service configuration changed during verification.");
            stage = "DeleteService";
            result = PortableCleanupResult.FromWin32(stage,
                DeleteService(service.DangerousGetHandle()) ? 0 : Marshal.GetLastWin32Error());
            // Both SCM handles are disposed before the caller starts StopCore.
        }
        catch (Win32Exception ex)
        {
            result = PortableCleanupResult.FromWin32(stage, ex.NativeErrorCode);
            BackendEmergencyLogger.Log(ex, $"PortableServiceCleanup: Stage={stage}, continuing normal stop.");
        }
        catch (Exception ex)
        {
            result = new(stage, "RefusedOrUncertain");
            BackendEmergencyLogger.Log(ex, $"PortableServiceCleanup: Stage={stage}, continuing normal stop.");
        }
        Log(result, windowsShutdown, caller);
    }

    internal static bool OwnsRegistration(string imagePath, string executable, uint registeredPid, int currentPid)
        => registeredPid == currentPid && registeredPid != 0
           && string.Equals(imagePath.Trim(), $"\"{Path.GetFullPath(executable)}\" --service", StringComparison.OrdinalIgnoreCase);

    private static void VerifyUserPolicies(bool windowsShutdown, PortableExitCaller? caller)
    {
        // An authenticated exclusive frontend exit concerns the current runtime:
        // require its hive and inspect every loaded user's policy. Offline
        // profiles cannot be current consumers (verified independently above).
        // OS shutdown has no live caller, so all retained profiles remain
        // authoritative and an unavailable hive must veto automatic deletion.
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var profiles = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList")
            ?? throw new IOException("Profile inventory unavailable.");
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        var sids = profiles.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-", StringComparison.Ordinal)
            || s.StartsWith("S-1-12-1-", StringComparison.Ordinal)).ToArray();
        if (sids.Length is 0 or > 128) throw new IOException("User policy inventory is unavailable or exceeds the shutdown budget.");
        if (!windowsShutdown && (caller is null || !sids.Contains(caller.Sid, StringComparer.OrdinalIgnoreCase)))
            throw new IOException("Authenticated frontend policy is absent from the profile inventory.");
        foreach (var sid in sids)
        {
            _ = new SecurityIdentifier(sid);
            using var hive = users.OpenSubKey(sid);
            VerifyUserPolicy(hive, requireLoaded: windowsShutdown || sid.Equals(caller!.Sid, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal static void VerifyUserPolicy(RegistryKey? hive, bool requireLoaded)
    {
        if (hive is null)
        {
            if (requireLoaded) throw new IOException("Required user policy is unavailable in an unloaded hive.");
            return;
        }
        using var policy = hive.OpenSubKey(@"Software\ContextMenuMgr\Frontend");
        var value = policy?.GetValue("StartWithWindows");
        if (value is not null && (value is not int number || number != 0
            || policy!.GetValueKind("StartWithWindows") != RegistryValueKind.DWord))
            throw new IOException("Autostart is enabled or its policy is uncertain.");
        using var run = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (run?.GetValue("ContextMenuManagerPlus.TrayHost") is not null)
            throw new IOException("Legacy autostart policy remains.");
    }

    private static void VerifyConsumers(bool windowsShutdown, PortableExitCaller? caller)
    {
        if (windowsShutdown) return; // Persisted policies govern OS-wide shutdown.
        if (!windowsShutdown && caller is null) throw new UnauthorizedAccessException("Missing frontend authorization.");
        var sessions = ReadUserSessions();
        var consumers = new List<int>();
        foreach (var name in new[] { "ContextMenuManagerPlus", "ContextMenuManagerPlus.TrayHost" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    if (process.HasExited) continue;
                    consumers.Add(process.Id);
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        if (!PortableServiceConsumers.IsExclusiveFrontend(caller!.ProcessId, caller.SessionId, sessions, consumers))
            throw new IOException($"Another logged-in session, frontend or TrayHost may use the service. "
                + $"CallerPID={caller.ProcessId}, CallerSession={caller.SessionId}, "
                + $"UserSessions=[{string.Join(',', sessions)}], ConsumerPIDs=[{string.Join(',', consumers)}].");
    }

    private static IReadOnlyCollection<int> ReadUserSessions()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessions, out var count))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSEnumerateSessions");
        try
        {
            var result = new List<int>();
            if (count < 0 || count > 128) throw new IOException("Session inventory is uncertain.");
            for (var index = 0; index < count; index++)
            {
                var session = Marshal.PtrToStructure<SessionInfo>(IntPtr.Add(sessions, index * Marshal.SizeOf<SessionInfo>()));
                if (session.Id == 0) continue;
                if (!WTSQuerySessionInformationW(IntPtr.Zero, session.Id, 5, out var buffer, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Query session user");
                try
                {
                    if (!string.IsNullOrEmpty(Marshal.PtrToStringUni(buffer)))
                        result.Add(session.Id);
                }
                finally { WTSFreeMemory(buffer); }
            }
            return result;
        }
        finally { WTSFreeMemory(sessions); }
    }

    private static void Log(PortableCleanupResult result, bool shutdown, PortableExitCaller? caller)
        => BackendEmergencyLogger.Log($"PortableServiceCleanup: Service={ServiceMetadata.ServiceName}, PID={Environment.ProcessId}, "
            + $"Trigger={(shutdown ? "WindowsShutdown" : "FrontendExit")}, CallerSid={caller?.Sid}, SessionId={caller?.SessionId}, "
            + $"Stage={result.Stage}, Result={result.Code}, Win32Error={result.Win32Error}, FullyRemovedVerified=False.");

    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo { public int Id; public IntPtr Name; public int State; }
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
