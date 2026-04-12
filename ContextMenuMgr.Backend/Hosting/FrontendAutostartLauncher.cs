using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Hosting;

// Launches the unprivileged tray frontend inside the active interactive user
// session. The backend service uses this instead of relying on a Run key so the
// tray app can stay silent on startup while still being controlled by the
// service lifecycle.
internal sealed class FrontendAutostartLauncher
{
    private const string FrontendPolicyKeyPath = @"Software\ContextMenuMgr\Frontend";
    private const string FrontendPolicyValueName = "StartWithWindows";
    private const string LegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValueName = "ContextMenuManager";

    private readonly string _frontendExePath;

    public FrontendAutostartLauncher(string baseDirectory)
    {
        _frontendExePath = Path.Combine(baseDirectory, "ContextMenuManager.exe");
    }

    public bool TryLaunchFrontendForActiveSession(int? sessionId = null)
    {
        if (!File.Exists(_frontendExePath))
        {
            return false;
        }

        var targetSessionId = sessionId ?? unchecked((int)NativeMethods.WTSGetActiveConsoleSessionId());
        if (targetSessionId < 0)
        {
            return false;
        }

        if (!TryGetUserSid(targetSessionId, out var userSid))
        {
            return false;
        }

        if (!IsAutostartEnabledForUser(userSid))
        {
            return false;
        }

        if (IsFrontendAlreadyRunning(targetSessionId))
        {
            return true;
        }

        return TryCreateFrontendProcess(targetSessionId);
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

        // One-time compatibility with older builds that stored the startup
        // choice by writing a Run key entry directly.
        using var legacyRunKey = Registry.Users.OpenSubKey($@"{userSid}\{LegacyRunKeyPath}", writable: false);
        return legacyRunKey?.GetValue(LegacyRunValueName) is string legacyCommand
               && !string.IsNullOrWhiteSpace(legacyCommand);
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

    private static bool IsFrontendAlreadyRunning(int sessionId)
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

    private bool TryCreateFrontendProcess(int sessionId)
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

            var commandLine = $"\"{_frontendExePath}\" --startup";
            var currentDirectory = Path.GetDirectoryName(_frontendExePath) ?? AppContext.BaseDirectory;

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
