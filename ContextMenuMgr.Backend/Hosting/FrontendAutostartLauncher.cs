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

internal sealed class FrontendAutostartLauncher
{
    private const string FrontendPolicyKeyPath = @"Software\ContextMenuMgr\Frontend";
    private const string FrontendPolicyValueName = "StartWithWindows";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _frontendExePath;
    private readonly string _trayHostExePath;

    public FrontendAutostartLauncher(string baseDirectory)
    {
        _frontendExePath = Path.Combine(baseDirectory, "ContextMenuManager.exe");
        _trayHostExePath = Path.Combine(baseDirectory, "ContextMenuManager.TrayHost.exe");
    }

    public bool TryLaunchTrayHostForActiveSession(int? sessionId = null)
    {
        if (!File.Exists(_trayHostExePath))
        {
            return false;
        }

        var targetSessionId = sessionId ?? GetActiveSessionId();
        if (targetSessionId < 0 || !TryGetUserSid(targetSessionId, out var userSid))
        {
            return false;
        }

        if (!IsAutostartEnabledForUser(userSid))
        {
            return false;
        }

        if (IsTrayHostRunning(targetSessionId))
        {
            return true;
        }

        return TryCreateUserProcess(targetSessionId, _trayHostExePath, string.Empty);
    }

    public bool TryShowMainWindowForActiveSession(int? sessionId = null)
        => TryOpenFrontendForActiveSession(
            new FrontendControlRequest { Command = FrontendControlCommand.ShowMainWindow },
            sessionId,
            "--show-main");

    public bool TryOpenApprovalsForActiveSession(string? focusItemId, int? sessionId = null)
        => TryOpenFrontendForActiveSession(
            new FrontendControlRequest
            {
                Command = FrontendControlCommand.OpenApprovals,
                FocusItemId = focusItemId
            },
            sessionId,
            BuildFrontendArguments("--open-approvals", focusItemId));

    public async Task<bool> TryShutdownFrontendForActiveSessionAsync(int? sessionId, CancellationToken cancellationToken)
    {
        var targetSessionId = sessionId ?? GetActiveSessionId();
        if (targetSessionId < 0 || !IsFrontendRunning(targetSessionId))
        {
            return true;
        }

        return await TrySendFrontendControlRequestAsync(
            new FrontendControlRequest { Command = FrontendControlCommand.Shutdown },
            cancellationToken);
    }

    public void KillFrontendProcessesForActiveSession(int? sessionId)
    {
        var targetSessionId = sessionId ?? GetActiveSessionId();
        foreach (var process in Process.GetProcessesByName("ContextMenuManager"))
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
        var targetSessionId = sessionId ?? GetActiveSessionId();
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

    private static int GetActiveSessionId()
    {
        var sessionId = unchecked((int)NativeMethods.WTSGetActiveConsoleSessionId());
        return sessionId == -1 ? -1 : sessionId;
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
        foreach (var process in Process.GetProcessesByName("ContextMenuManager.TrayHost"))
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
        foreach (var process in Process.GetProcessesByName("ContextMenuManager"))
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

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DuplicateTokenEx(
            SafeHandle existingTokenHandle,
            uint desiredAccess,
            IntPtr tokenAttributes,
            SECURITY_IMPERSONATION_LEVEL impersonationLevel,
            TOKEN_TYPE tokenType,
            out IntPtr duplicateTokenHandle);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateEnvironmentBlock(
            out IntPtr environment,
            SafeHandle token,
            [MarshalAs(UnmanagedType.Bool)] bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyEnvironmentBlock(IntPtr environment);

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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        public enum TOKEN_TYPE
        {
            TokenPrimary = 1,
            TokenImpersonation = 2
        }

        public enum SECURITY_IMPERSONATION_LEVEL
        {
            SecurityAnonymous,
            SecurityIdentification,
            SecurityImpersonation,
            SecurityDelegation
        }

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
