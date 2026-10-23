using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Resolves the user that owns a named-pipe request without relying on the
/// service process HKCU/profile.
/// </summary>
public sealed class BackendUserContextResolver
{
    private readonly FileLogger _logger;

    public BackendUserContextResolver(FileLogger logger)
    {
        _logger = logger;
    }

    public BackendUserContext? TryResolveFromPipeClient(NamedPipeServerStream stream)
    {
        _logger.LogFireAndForget("UserContextResolveStart: Source=PipeClient.");
        try
        {
            string? sid = null;
            stream.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                sid = identity.User?.Value;
                // Do not read WindowsIdentity.Name here. Its managed
                // RunImpersonated caches this native pipe token in AsyncLocal;
                // RunAsClient's native RevertToSelf cannot clear that cache.
                // A later await would then impersonate the client again.
            });

            // Name is display metadata, resolved only after native impersonation
            // has ended. Authentication continues to use the captured token SID.
            var userName = sid;
            if (!string.IsNullOrWhiteSpace(sid))
            {
                try { userName = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
                catch (IdentityNotMappedException) { }
            }
            var sessionId = TryResolvePipeClientSessionId(stream);
            var context = string.IsNullOrWhiteSpace(sid)
                ? null
                : CreateContext(sid, userName ?? sid, sessionId);
            _logger.LogFireAndForget(
                context is null
                    ? "UserContextResolveFailure: Source=PipeClient, Reason=MissingSid."
                    : $"UserContextResolveSuccess: Source=PipeClient, Sid={context.Sid}, UserName={context.UserName}, ProfilePath={context.ProfilePath}, SessionId={context.SessionId?.ToString() ?? "<null>"}.");
            return context;
        }
        catch (Exception ex)
        {
            _ = _logger.LogAsync(RuntimeLogLevel.Warning, $"Unable to resolve pipe client user context: {ex}", CancellationToken.None);
            return null;
        }
    }

    public BackendUserContext? TryResolveFromPipeClientWithSessionFallback(NamedPipeServerStream stream)
    {
        var pipeContext = TryResolveFromPipeClient(stream);
        if (pipeContext?.SessionId is not null)
        {
            return pipeContext;
        }

        var fallbackContext = TryResolveInteractiveUserFallback();

        if (pipeContext is null)
        {
            return fallbackContext;
        }

        if (fallbackContext is not null
            && string.Equals(fallbackContext.Sid, pipeContext.Sid, StringComparison.OrdinalIgnoreCase))
        {
            return fallbackContext;
        }

        return pipeContext;
    }

    public BackendUserContext? TryResolveInteractiveUserFallback()
    {
        var activeConsoleSessionId = unchecked((int)NativeMethods.WTSGetActiveConsoleSessionId());
        _logger.LogFireAndForget($"UserContextResolveStart: Source=InteractiveFallback, ActiveConsoleSessionId={activeConsoleSessionId}.");
        if (TryResolveSession(activeConsoleSessionId, out var context))
        {
            _logger.LogFireAndForget($"UserContextResolveSuccess: Source=ActiveConsole, Sid={context!.Sid}, UserName={context.UserName}, ProfilePath={context.ProfilePath}, SessionId={context.SessionId}.");
            return context;
        }

        if (!NativeMethods.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessionInfoPtr, out var count))
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"UserContextResolveFailure: Source=WTSEnumerateSessions, LastWin32Error={Marshal.GetLastWin32Error()}.");
            return null;
        }

        try
        {
            var dataSize = Marshal.SizeOf<NativeMethods.WTS_SESSION_INFO>();
            BackendUserContext? connected = null;
            for (var index = 0; index < count; index++)
            {
                var current = IntPtr.Add(sessionInfoPtr, index * dataSize);
                var sessionInfo = Marshal.PtrToStructure<NativeMethods.WTS_SESSION_INFO>(current);
                _logger.LogFireAndForget($"UserContextSessionCandidate: SessionId={sessionInfo.SessionID}, State={sessionInfo.State}.");
                if (!TryResolveSession(sessionInfo.SessionID, out var userContext))
                {
                    continue;
                }

                if (sessionInfo.State == NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive)
                {
                    _logger.LogFireAndForget($"UserContextResolveSuccess: Source=WTSEnumerate Active, Sid={userContext!.Sid}, UserName={userContext.UserName}, ProfilePath={userContext.ProfilePath}, SessionId={userContext.SessionId}.");
                    return userContext;
                }

                if (connected is null && sessionInfo.State == NativeMethods.WTS_CONNECTSTATE_CLASS.WTSConnected)
                {
                    connected = userContext;
                }
            }

            if (connected is not null)
            {
                _logger.LogFireAndForget($"UserContextResolveSuccess: Source=WTSEnumerate Connected, Sid={connected.Sid}, UserName={connected.UserName}, ProfilePath={connected.ProfilePath}, SessionId={connected.SessionId}.");
            }
            else
            {
                _logger.LogFireAndForget(RuntimeLogLevel.Warning, "UserContextResolveFailure: Source=InteractiveFallback, Reason=NoActiveOrConnectedUserToken.");
            }

            return connected;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(sessionInfoPtr);
        }
    }

    // Monitoring may have several logged-in users even when only one console is active.
    // Tokens establish identity; a loaded HKEY_USERS hive is validated separately by OpenWith.
    internal IReadOnlyList<BackendUserContext> ResolveLoggedInUsers()
    {
        if (!NativeMethods.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessions, out var count)) return [];
        try
        {
            var result = new List<BackendUserContext>();
            var size = Marshal.SizeOf<NativeMethods.WTS_SESSION_INFO>();
            for (var index = 0; index < Math.Min(count, 128); index++)
            {
                var session = Marshal.PtrToStructure<NativeMethods.WTS_SESSION_INFO>(IntPtr.Add(sessions, index * size));
                if (session.State is not (NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive
                    or NativeMethods.WTS_CONNECTSTATE_CLASS.WTSConnected or NativeMethods.WTS_CONNECTSTATE_CLASS.WTSDisconnected)) continue;
                if (TryResolveSession(session.SessionID, out var context)) result.Add(context!);
            }
            return result.DistinctBy(c => c.Sid, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { NativeMethods.WTSFreeMemory(sessions); }
    }

    private int? TryResolvePipeClientSessionId(NamedPipeServerStream stream)
    {
        try
        {
            if (!NativeMethods.GetNamedPipeClientProcessId(stream.SafePipeHandle, out var clientProcessId))
            {
                _logger.LogFireAndForget(
                    RuntimeLogLevel.Warning,
                    $"UserContextPipeClientProcessFailure: LastWin32Error={Marshal.GetLastWin32Error()}.");
                return null;
            }

            using var process = Process.GetProcessById(checked((int)clientProcessId));
            var sessionId = process.SessionId;

            _logger.LogFireAndForget(
                $"UserContextPipeClientProcessResolved: ClientProcessId={clientProcessId}, SessionId={sessionId}.");

            return sessionId;
        }
        catch (Exception ex)
        {
            _logger.LogFireAndForget(
                RuntimeLogLevel.Warning,
                $"UserContextPipeClientSessionFailure: Exception={ex}");
            return null;
        }
    }

    private bool TryResolveSession(int sessionId, out BackendUserContext? context)
    {
        context = null;
        if (sessionId < 0 || !NativeMethods.WTSQueryUserToken(sessionId, out var tokenHandle))
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"UserContextSessionTokenFailure: SessionId={sessionId}, LastWin32Error={Marshal.GetLastWin32Error()}.");
            return false;
        }

        using var token = new SafeAccessTokenHandle(tokenHandle);
        try
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            var sid = identity.User?.Value;
            if (string.IsNullOrWhiteSpace(sid))
            {
                _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"UserContextSessionFailure: SessionId={sessionId}, Reason=MissingSid.");
                return false;
            }

            context = CreateContext(sid, identity.Name, sessionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"UserContextSessionFailure: SessionId={sessionId}, Exception={ex}");
            return false;
        }
    }

    private static BackendUserContext CreateContext(string sid, string userName, int? sessionId)
    {
        var profilePath = GetProfilePath(sid);
        if (string.IsNullOrWhiteSpace(profilePath) || !Directory.Exists(profilePath))
        {
            // This exception is logged by the caller with the attempted session/source.
            throw new InvalidOperationException($"Unable to resolve profile path for {sid}.");
        }

        return new BackendUserContext(
            sid,
            userName,
            profilePath,
            Path.Combine(profilePath, @"AppData\Local"),
            Path.Combine(profilePath, @"AppData\Roaming"),
            sessionId);
    }

    private static string GetProfilePath(string sid)
    {
        using var profileKey = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}", writable: false);
        return Environment.ExpandEnvironmentVariables(profileKey?.GetValue("ProfileImagePath")?.ToString() ?? string.Empty);
    }

    private sealed class SafeAccessTokenHandle : SafeHandle
    {
        public SafeAccessTokenHandle(IntPtr handle)
            : base(IntPtr.Zero, ownsHandle: true)
        {
            SetHandle(handle);
        }

        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNamedPipeClientProcessId(
            SafePipeHandle pipe,
            out uint clientProcessId);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSEnumerateSessionsW(
            IntPtr hServer,
            int reserved,
            int version,
            out IntPtr ppSessionInfo,
            out int pCount);

        [DllImport("wtsapi32.dll")]
        public static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        public enum WTS_CONNECTSTATE_CLASS
        {
            WTSActive,
            WTSConnected,
            WTSConnectQuery,
            WTSShadow,
            WTSDisconnected,
            WTSIdle,
            WTSListen,
            WTSReset,
            WTSDown,
            WTSInit
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WTS_SESSION_INFO
        {
            public int SessionID;
            public string pWinStationName;
            public WTS_CONNECTSTATE_CLASS State;
        }
    }
}
