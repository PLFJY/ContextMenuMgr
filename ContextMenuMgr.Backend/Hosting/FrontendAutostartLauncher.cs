using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Hosting;

/// <summary>
/// Represents the frontend Autostart Launcher.
/// </summary>
internal sealed class FrontendAutostartLauncher
{
    private const string FrontendPolicyKeyPath = @"Software\ContextMenuMgr\Frontend";
    private const string FrontendPolicyValueName = "StartWithWindows";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _frontendExePath;
    private readonly string _trayHostExePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="FrontendAutostartLauncher"/> class.
    /// </summary>
    public FrontendAutostartLauncher(string baseDirectory)
    {
        _frontendExePath = Path.Combine(baseDirectory, "ContextMenuManagerPlus.exe");
        _trayHostExePath = Path.Combine(baseDirectory, "ContextMenuManagerPlus.TrayHost.exe");
    }

    /// <summary>
    /// Attempts to launch Tray Host For Active Session.
    /// </summary>
    public bool TryLaunchTrayHostForActiveSession(int? sessionId = null, bool requireAutostartPolicy = true)
    {
        if (!File.Exists(_trayHostExePath))
        {
            return false;
        }

        var targetSessionId = sessionId ?? GetBestInteractiveSessionId();
        if (targetSessionId < 0 || !TryGetUserSid(targetSessionId, out var userSid))
        {
            return false;
        }

        if (requireAutostartPolicy && !IsAutostartEnabledForUser(userSid))
        {
            return false;
        }

        if (IsTrayHostRunning(targetSessionId))
        {
            return true;
        }

        // The tray host lives in the user's session, so service-side code must
        // cross the session boundary with a user token before starting it.
        return TryCreateUserProcess(targetSessionId, _trayHostExePath, string.Empty);
    }

    /// <summary>
    /// Attempts to show Main Window For Active Session.
    /// </summary>
    public bool TryShowMainWindowForActiveSession(int? sessionId = null)
        => TryOpenFrontendForActiveSession(
            new FrontendControlRequest { Command = FrontendControlCommand.ShowMainWindow },
            sessionId,
            "--show-main");

    /// <summary>
    /// Attempts to open Approvals For Active Session.
    /// </summary>
    public bool TryOpenApprovalsForActiveSession(string? focusItemId, int? sessionId = null)
        => TryOpenFrontendForActiveSession(
            new FrontendControlRequest
            {
                Command = FrontendControlCommand.OpenApprovals,
                FocusItemId = focusItemId
            },
            sessionId,
            BuildFrontendArguments("--open-approvals", focusItemId));

    /// <summary>
    /// Attempts to shutdown Frontend For Active Session Async.
    /// </summary>
    public async Task<bool> TryShutdownFrontendForActiveSessionAsync(int? sessionId, CancellationToken cancellationToken)
    {
        var targetSessionId = sessionId ?? GetBestInteractiveSessionId();
        if (targetSessionId < 0 || !IsFrontendRunning(targetSessionId))
        {
            return true;
        }

        return await TrySendFrontendControlRequestAsync(
            new FrontendControlRequest { Command = FrontendControlCommand.Shutdown },
            cancellationToken);
    }

    /// <summary>
    /// Executes kill Frontend Processes For Active Session.
    /// </summary>
    public void KillFrontendProcessesForActiveSession(int? sessionId)
    {
        var targetSessionId = sessionId ?? GetBestInteractiveSessionId();
        foreach (var process in Process.GetProcessesByName("ContextMenuManagerPlus"))
        {
            try
            {
                if (targetSessionId < 0 || process.SessionId == targetSessionId)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private bool TryOpenFrontendForActiveSession(FrontendControlRequest request, int? sessionId, string startupArguments)
    {
        var targetSessionId = sessionId ?? GetBestInteractiveSessionId();
        if (targetSessionId < 0 || !File.Exists(_frontendExePath))
        {
            return false;
        }

        if (IsFrontendRunning(targetSessionId)
            && TrySendFrontendControlRequestAsync(request, CancellationToken.None).GetAwaiter().GetResult())
        {
            return true;
        }

        return TryCreateUserProcess(targetSessionId, _frontendExePath, startupArguments);
    }

    private static string BuildFrontendArguments(string command, string? focusItemId)
    {
        if (string.IsNullOrWhiteSpace(focusItemId))
        {
            return command;
        }

        return $"{command} --focus-item \"{focusItemId}\"";
    }

    private static bool IsAutostartEnabledForUser(string userSid)
    {
        using var policyKey = Registry.Users.OpenSubKey($@"{userSid}\{FrontendPolicyKeyPath}", writable: false);
        var policyValue = policyKey?.GetValue(FrontendPolicyValueName);
        if (policyValue is int intValue)
        {
            return intValue != 0;
        }

        if (policyValue is string stringValue && int.TryParse(stringValue, out var parsed))
        {
            return parsed != 0;
        }

        return false;
    }

    private static int GetBestInteractiveSessionId()
    {
        var consoleSessionId = unchecked((int)NativeMethods.WTSGetActiveConsoleSessionId());
        if (consoleSessionId != -1 && TryGetUserSid(consoleSessionId, out _))
        {
            return consoleSessionId;
        }

        if (!NativeMethods.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessionInfoPtr, out var count))
        {
            return -1;
        }

        try
        {
            var dataSize = Marshal.SizeOf<NativeMethods.WTS_SESSION_INFO>();
            var connectedCandidate = -1;

            for (var index = 0; index < count; index++)
            {
                var current = IntPtr.Add(sessionInfoPtr, index * dataSize);
                var sessionInfo = Marshal.PtrToStructure<NativeMethods.WTS_SESSION_INFO>(current);

                if (sessionInfo.SessionID == -1)
                {
                    continue;
                }

                if (!TryGetUserSid(sessionInfo.SessionID, out _))
                {
                    continue;
                }

                if (sessionInfo.State == NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive)
                {
                    // Prefer an actively logged-on desktop session so foreground
                    // UI processes land in the place the user can actually see.
                    return sessionInfo.SessionID;
                }

                if (connectedCandidate < 0 && sessionInfo.State == NativeMethods.WTS_CONNECTSTATE_CLASS.WTSConnected)
                {
                    connectedCandidate = sessionInfo.SessionID;
                }
            }

            return connectedCandidate;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(sessionInfoPtr);
        }
    }

    private static bool TryGetUserSid(int sessionId, out string sid)
    {
        sid = string.Empty;
        if (!NativeMethods.WTSQueryUserToken(sessionId, out var tokenHandle))
        {
            return false;
        }

        using var token = new SafeAccessTokenHandle(tokenHandle);
        try
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            sid = identity.User?.Value ?? string.Empty;
            return !string.IsNullOrWhiteSpace(sid);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTrayHostRunning(int sessionId)
    {
        foreach (var process in Process.GetProcessesByName("ContextMenuManagerPlus.TrayHost"))
        {
            try
            {
                if (process.SessionId == sessionId)
                {
                    return true;
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static bool IsFrontendRunning(int sessionId)
    {
        foreach (var process in Process.GetProcessesByName("ContextMenuManagerPlus"))
        {
            try
            {
                if (process.SessionId == sessionId)
                {
                    return true;
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private bool TryCreateUserProcess(int sessionId, string executablePath, string arguments)
    {
        if (!NativeMethods.WTSQueryUserToken(sessionId, out var userTokenRaw))
        {
            return false;
        }

        using var userToken = new SafeAccessTokenHandle(userTokenRaw);
        if (!NativeMethods.DuplicateTokenEx(
                userToken,
                NativeMethods.TOKEN_ALL_ACCESS,
                IntPtr.Zero,
                NativeMethods.SECURITY_IMPERSONATION_LEVEL.SecurityIdentification,
                NativeMethods.TOKEN_TYPE.TokenPrimary,
                out var primaryTokenRaw))
        {
            return false;
        }

        using var primaryToken = new SafeAccessTokenHandle(primaryTokenRaw);
        if (!NativeMethods.CreateEnvironmentBlock(out var environmentBlock, primaryToken, false))
        {
            environmentBlock = IntPtr.Zero;
        }

        try
        {
            var startupInfo = new NativeMethods.STARTUPINFO
            {
                cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>(),
                lpDesktop = @"winsta0\default"
            };

            var currentDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
            var commandLine = $"\"{executablePath}\" {arguments}".Trim();

            var created = NativeMethods.CreateProcessAsUser(
                primaryToken,
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                environmentBlock,
                currentDirectory,
                ref startupInfo,
                out var processInformation);

            if (!created)
            {
                return false;
            }

            NativeMethods.CloseHandle(processInformation.hThread);
            NativeMethods.CloseHandle(processInformation.hProcess);
            return true;
        }
        finally
        {
            if (environmentBlock != IntPtr.Zero)
            {
                NativeMethods.DestroyEnvironmentBlock(environmentBlock);
            }
        }
    }

    private static async Task<bool> TrySendFrontendControlRequestAsync(FrontendControlRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new NamedPipeClientStream(".", PipeConstants.FrontendControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await stream.ConnectAsync(500, cancellationToken);

            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions)).WaitAsync(cancellationToken);
            var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
            if (line is null)
            {
                return false;
            }

            var response = JsonSerializer.Deserialize<FrontendControlResponse>(line, JsonOptions);
            return response?.Success == true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class SafeAccessTokenHandle : SafeHandle
    {
        /// <summary>
        /// Executes safe Access Token Handle.
        /// </summary>
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
        public const uint TOKEN_ALL_ACCESS = 0xF01FF;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        /// <summary>
        /// Executes wTS Get Active Console Session Id.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WTSGetActiveConsoleSessionId();

        /// <summary>
        /// Executes wTS Query User Token.
        /// </summary>
        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

        /// <summary>
        /// Executes wTS Enumerate Sessions W.
        /// </summary>
        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSEnumerateSessionsW(
            IntPtr hServer,
            int Reserved,
            int Version,
            out IntPtr ppSessionInfo,
            out int pCount);

        /// <summary>
        /// Executes wTS Free Memory.
        /// </summary>
        [DllImport("wtsapi32.dll")]
        public static extern void WTSFreeMemory(IntPtr memory);

        /// <summary>
        /// Executes duplicate Token Ex.
        /// </summary>
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DuplicateTokenEx(
            SafeHandle existingTokenHandle,
            uint desiredAccess,
            IntPtr tokenAttributes,
            SECURITY_IMPERSONATION_LEVEL impersonationLevel,
            TOKEN_TYPE tokenType,
            out IntPtr duplicateTokenHandle);

        /// <summary>
        /// Creates environment Block.
        /// </summary>
        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateEnvironmentBlock(
            out IntPtr environment,
            SafeHandle token,
            [MarshalAs(UnmanagedType.Bool)] bool inherit);

        /// <summary>
        /// Executes destroy Environment Block.
        /// </summary>
        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyEnvironmentBlock(IntPtr environment);

        /// <summary>
        /// Creates process As User.
        /// </summary>
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessAsUser(
            SafeHandle token,
            string? applicationName,
            string commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref STARTUPINFO startupInfo,
            out PROCESS_INFORMATION processInformation);

        /// <summary>
        /// Executes close Handle.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        /// <summary>
        /// Defines the available tOKEN_TYPE values.
        /// </summary>
        public enum TOKEN_TYPE
        {
            TokenPrimary = 1,
            TokenImpersonation = 2
        }

        /// <summary>
        /// Defines the available sECURITY_IMPERSONATION_LEVEL values.
        /// </summary>
        public enum SECURITY_IMPERSONATION_LEVEL
        {
            SecurityAnonymous,
            SecurityIdentification,
            SecurityImpersonation,
            SecurityDelegation
        }

        /// <summary>
        /// Defines the available wTS_CONNECTSTATE_CLASS values.
        /// </summary>
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

        /// <summary>
        /// Represents the wTS_SESSION_INFO.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WTS_SESSION_INFO
        {
            public int SessionID;
            public IntPtr pWinStationName;
            public WTS_CONNECTSTATE_CLASS State;
        }

        /// <summary>
        /// Represents the sTARTUPINFO.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        /// <summary>
        /// Represents the pROCESS_INFORMATION.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }
    }
}
