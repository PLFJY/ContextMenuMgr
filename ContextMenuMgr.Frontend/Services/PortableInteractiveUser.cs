#if CONTEXTMENUMGR_IN_PROCESS
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>Owns the verified desktop user's non-elevated token for the lifetime of the local host.</summary>
internal sealed class PortableInteractiveUser : IDisposable
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenImpersonate = 0x0004;
    private const uint TokenQuery = 0x0008;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;
    private const int KeyRead = 0x20019;
    private const int KeyWow64_64Key = 0x0100;
    private const int KeyWow64_32Key = 0x0200;
    private const uint KnownFolderDontVerify = 0x00004000;
    private const int WindowsPathCapacity = 32768;

    private static readonly SecurityIdentifier InteractiveGroup = new(WellKnownSidType.InteractiveSid, null);
    private static readonly SecurityIdentifier MediumIntegrity = new("S-1-16-8192");
    private static readonly Guid LocalAppDataFolder = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    private static readonly Guid RoamingAppDataFolder = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D");

    private readonly SafeAccessTokenHandle _token;

    private PortableInteractiveUser(SafeAccessTokenHandle token, BackendUserContext context)
    {
        _token = token;
        Context = context;
    }

    public BackendUserContext Context { get; }

    public static PortableInteractiveUser Resolve()
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var processIdentity = WindowsIdentity.GetCurrent();
        if (ReadTokenInt(processIdentity.AccessToken, TokenInformation.Elevation) == 0 || currentProcess.SessionId <= 0)
            throw new InvalidOperationException("Start Portable from an interactive Explorer desktop and approve its administrator prompt.");

        int sessionId = currentProcess.SessionId;
        var shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero || GetWindowThreadProcessId(shellWindow, out uint shellPid) == 0)
            throw new InvalidOperationException("Windows Explorer is unavailable on the current desktop. Start or restart Explorer and reopen Portable.");

        // The window and image establish which process supplies the token. The SID from
        // the session logon independently binds that token to the intended desktop user.
        string userName = ReadSessionUser(sessionId);
        var sessionSid = (SecurityIdentifier)new NTAccount(userName).Translate(typeof(SecurityIdentifier));
        using var shell = OpenProcess(ProcessQueryLimitedInformation, false, shellPid);
        if (shell.IsInvalid) throw LastError("Open the current desktop shell process");
        var image = new StringBuilder(WindowsPathCapacity);
        uint imageLength = (uint)image.Capacity;
        if (!QueryFullProcessImageName(shell, 0, image, ref imageLength)) throw LastError("Read the desktop shell executable path");
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!string.Equals(image.ToString(), explorer, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This Portable build requires the Windows Explorer desktop shell in its own session.");

        if (!OpenProcessToken(shell, TokenDuplicate | TokenQuery, out var shellToken))
            throw LastError("Open the desktop shell access token");
        using (shellToken)
        {
            if (!DuplicateTokenEx(shellToken, TokenAssignPrimary | TokenDuplicate | TokenImpersonate | TokenQuery,
                    IntPtr.Zero, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out var token))
                throw LastError("Retain the desktop user's token");
            try
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                if (identity.User is not { } tokenSid || !sessionSid.Equals(tokenSid)
                    || !new WindowsPrincipal(identity).IsInRole(InteractiveGroup)
                    || ReadTokenInt(token, TokenInformation.SessionId) != sessionId
                    || ReadTokenInt(token, TokenInformation.Elevation) != 0
                    || ReadTokenInt(token, TokenInformation.UIAccess) != 0
                    || !ReadIntegritySid(token).Equals(MediumIntegrity))
                {
                    throw new InvalidOperationException("Explorer must be running as the logged-on user at normal integrity in this session. Sign in with UAC enabled and reopen Portable.");
                }

                if (GetShellWindow() != shellWindow || GetWindowThreadProcessId(shellWindow, out uint confirmedPid) == 0 || confirmedPid != shellPid)
                    throw new InvalidOperationException("The desktop shell changed while validating its identity. Reopen Portable after Explorer has restarted.");

                if (!IsHiveLoaded(sessionSid.Value))
                    throw new InvalidOperationException("The desktop user's registry hive is unavailable. Sign in again before reopening Portable.");
                var profile = ReadProfile(token);
                if (!Directory.Exists(profile))
                    throw new InvalidOperationException("The desktop user's profile directory is unavailable.");

                var (local, roaming) = WindowsIdentity.RunImpersonated(token,
                    () => (ReadKnownFolder(token, LocalAppDataFolder), ReadKnownFolder(token, RoamingAppDataFolder)));
                return new PortableInteractiveUser(token, new BackendUserContext(sessionSid, userName, profile, local, roaming, sessionId));
            }
            catch
            {
                token.Dispose();
                throw;
            }
        }
    }

    public BackendUserContext? TryGetContext() =>
        !_token.IsClosed && IsHiveLoaded(Context.Sid) && Directory.Exists(Context.ProfilePath) ? Context : null;

    public RegistryKey OpenClassesRoot(RegistryView view)
    {
        if (TryGetContext() is null)
            throw new InvalidOperationException("The verified desktop user's profile is no longer available.");
        var desiredAccess = KeyRead | (view switch
        {
            RegistryView.Registry64 => KeyWow64_64Key,
            RegistryView.Registry32 => KeyWow64_32Key,
            _ => 0
        });
        int result = RegOpenUserClassesRoot(_token, 0, desiredAccess, out var handle);
        if (result != 0) throw new Win32Exception(result, "Open the desktop user's merged Classes registry view.");
        try
        {
            return RegistryKey.FromHandle(handle, view);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public bool TryOpenClassesRoot(BackendUserContext context, FileLogger logger,
        out IFrontendClassesRoot? root, out string? failure)
    {
        root = null;
        try
        {
            if (!Context.UserSid.Equals(context.UserSid) || Context.SessionId != context.SessionId)
                throw new InvalidOperationException("The requested SID and session do not match the verified desktop user.");
            var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            root = new PortableClassesRoot(OpenClassesRoot(view));
            failure = null;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            failure = ex.Message;
            logger.LogFireAndForget(RuntimeLogLevel.Warning, $"ShellNewMergedClassesOpenFailed: Hosting=InProcess, Error={ex.Message}.");
            return false;
        }
    }

    public string ExpandEnvironmentVariables(string value)
    {
        if (!value.Contains('%')) return value;
        var expanded = new StringBuilder(WindowsPathCapacity);
        if (!ExpandEnvironmentStringsForUser(_token, value, expanded, (uint)expanded.Capacity))
            throw LastError("Expand the desktop user's environment variables");
        return expanded.ToString();
    }

    public string ResolveUserPath(string value)
    {
        var expanded = ExpandEnvironmentVariables(value);
        if (!Path.IsPathFullyQualified(expanded))
            throw new InvalidOperationException("A desktop-user data path must resolve to an absolute path.");
        return Path.GetFullPath(expanded);
    }

    public Process StartNonElevatedProcess(string executablePath, IEnumerable<string> arguments, string workingDirectory)
    {
        if (TryGetContext() is null)
            throw new InvalidOperationException("Deep Analysis is unavailable because the verified desktop user session has ended.");
        var commandLine = new StringBuilder(QuoteArgument(executablePath));
        foreach (var argument in arguments) commandLine.Append(' ').Append(QuoteArgument(argument));
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default" };
        if (!CreateProcessWithToken(_token, 0, executablePath, commandLine, 0x00000400,
                IntPtr.Zero, workingDirectory, ref startup, out var processInfo))
            throw LastError("Start Deep Analysis under the non-elevated desktop user token");
        CloseHandle(processInfo.Thread);
        CloseHandle(processInfo.Process);
        try { return Process.GetProcessById(checked((int)processInfo.ProcessId)); }
        catch
        {
            using var handle = OpenProcess(0x0001, false, processInfo.ProcessId);
            if (!handle.IsInvalid) TerminateProcess(handle, 1);
            throw;
        }
    }

    public void CreatePrivateDirectory(string directory)
    {
        if (TryGetContext() is null)
            throw new InvalidOperationException("The verified desktop user's profile is no longer available.");
        var sddl = $"D:P(A;OICI;FA;;;{Context.Sid})(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw LastError("Create the Deep Analysis directory access rules");
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            if (!CreateDirectory(directory, ref attributes))
                throw LastError("Create the private Deep Analysis operation directory");
        }
        finally { LocalFree(descriptor); }
    }

    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1).Append(character);
            else result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public void Dispose() => _token.Dispose();

    private static bool IsHiveLoaded(string sid)
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
        using var user = users.OpenSubKey(sid, writable: false);
        return user is not null;
    }

    private static string ReadSessionUser(int sessionId)
    {
        var account = ReadSessionText(sessionId, 5 /* WTSUserName */);
        var domain = ReadSessionText(sessionId, 7 /* WTSDomainName */);
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(domain))
            throw new InvalidOperationException("Windows could not identify the logged-on user for this session. Sign in again before reopening Portable.");
        return new NTAccount(domain, account).Value;
    }

    private static string ReadSessionText(int sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
            throw LastError("Read the logged-on user for the current Windows session");
        try { return Marshal.PtrToStringUni(buffer) ?? string.Empty; }
        finally { WTSFreeMemory(buffer); }
    }

    private static int ReadTokenInt(SafeAccessTokenHandle token, TokenInformation infoClass)
    {
        if (!GetTokenInformation(token, infoClass, out int value, sizeof(int), out _))
            throw LastError($"Read token {infoClass}");
        return value;
    }

    private static SecurityIdentifier ReadIntegritySid(SafeAccessTokenHandle token)
    {
        _ = GetTokenInformation(token, TokenInformation.IntegrityLevel, IntPtr.Zero, 0, out int size);
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || size <= 0)
            throw LastError("Determine the size of the token integrity label");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenInformation.IntegrityLevel, buffer, size, out _))
                throw LastError("Read the desktop token integrity label");
            // TOKEN_MANDATORY_LABEL begins with the SID pointer from SID_AND_ATTRIBUTES.
            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ReadProfile(SafeAccessTokenHandle token)
    {
        uint size = 0;
        _ = GetUserProfileDirectory(token, null, ref size);
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || size == 0 || size > WindowsPathCapacity)
            throw LastError("Determine the desktop user's profile path length");
        var profile = new StringBuilder((int)size);
        if (!GetUserProfileDirectory(token, profile, ref size)) throw LastError("Read the desktop user's profile path");
        return RequireFullPath(profile.ToString());
    }

    private static string ReadKnownFolder(SafeAccessTokenHandle token, Guid folder)
    {
        int result = SHGetKnownFolderPath(in folder, KnownFolderDontVerify, token, out var path);
        try
        {
            Marshal.ThrowExceptionForHR(result);
            return RequireFullPath(Marshal.PtrToStringUni(path));
        }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    private static string RequireFullPath(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        ? Path.GetFullPath(path)
        : throw new InvalidOperationException("Windows returned an invalid desktop-user profile or AppData path.");

    private static Win32Exception LastError(string action)
    {
        var error = new Win32Exception(Marshal.GetLastWin32Error());
        return new Win32Exception(error.NativeErrorCode, $"{action}. {error.Message}");
    }

    private sealed class PortableClassesRoot(RegistryKey classesRoot) : IFrontendClassesRoot
    {
        public RegistryKey ClassesRoot { get; } = classesRoot;

        public void Dispose() => ClassesRoot.Dispose();
    }

    private enum TokenInformation
    {
        SessionId = 12,
        Elevation = 20,
        IntegrityLevel = 25,
        UIAccess = 26
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle duplicate);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessWithTokenW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithToken(SafeAccessTokenHandle token, uint logonFlags,
        string applicationName, StringBuilder commandLine, uint creationFlags, IntPtr environment,
        string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision,
        out IntPtr securityDescriptor, out uint size);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, TokenInformation infoClass,
        out int data, int size, out int required);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, TokenInformation infoClass,
        IntPtr data, int size, out int required);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("userenv.dll", EntryPoint = "GetUserProfileDirectoryW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserProfileDirectory(SafeAccessTokenHandle token, StringBuilder? path, ref uint size);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, SafeAccessTokenHandle token, out IntPtr path);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int RegOpenUserClassesRoot(SafeAccessTokenHandle token, int options, int access, out SafeRegistryHandle key);

    [DllImport("userenv.dll", EntryPoint = "ExpandEnvironmentStringsForUserW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExpandEnvironmentStringsForUser(SafeAccessTokenHandle token, string source, StringBuilder destination, uint size);
}
#endif
