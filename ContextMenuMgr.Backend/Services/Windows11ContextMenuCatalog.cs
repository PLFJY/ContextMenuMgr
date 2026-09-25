using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Represents the windows11 Context Menu Catalog.
/// </summary>
internal sealed class Windows11ContextMenuCatalog
{
    private const string SystemCommandStorePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell";
    private const string UserBlockedPathSuffix = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string MachineBlockedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private static readonly HashSet<string> SupportedSystemCommandKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows.SendToMyPhone",
        "Windows.Share"
    };

    private readonly FileLogger? _logger;
    private readonly PackagedContextMenuScanCache _packageScanCache;

    public Windows11ContextMenuCatalog(FileLogger? logger = null)
    {
        _logger = logger;
        _packageScanCache = new PackagedContextMenuScanCache(
            sid => PackagedContextMenuDiscovery.FindForUser(sid, _logger, CancellationToken.None));
    }

    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>
    /// Executes enumerate Entries Async.
    /// </summary>
    public async Task<IReadOnlyList<ContextMenuEntry>> EnumerateEntriesAsync(CancellationToken cancellationToken, BackendUserContext? userContext = null)
    {
        var userSid = userContext?.Sid;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            // 只有在没有提供用户上下文时才回退到交互式用户检测
            // 这不应该发生，因为 NamedPipeBackendServer 应该总是传递 userContext
            userSid = TryGetBestInteractiveUserSid();
        }

        if (!IsSupported || string.IsNullOrWhiteSpace(userSid))
        {
            _logger?.LogFireAndForget($"Win11ContextMenuEnumerate: IsSupported={IsSupported}, UserSid={userSid ?? "<null>"}, Result=Skipped.");
            return [];
        }

        var items = new ConcurrentDictionary<string, ContextMenuEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var commandEntry in EnumerateSystemCommandStoreEntries())
        {
            items[commandEntry.Id] = commandEntry;
        }

        var definitions = await _packageScanCache.GetAsync(userSid, cancellationToken);
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var blockedState = GetBlockedState(definition.Clsid, userSid);
                foreach (var category in MapCategories(definition.ContextTypes))
                {
                    var entry = CreateEntry(definition, category, blockedState, userSid);
                    items[entry.Id] = entry;
                    _logger?.LogFireAndForget($"Win11ContextMenuEntry: PackageFullName={definition.Package.FullName}, DisplayName={definition.DisplayName}, Clsid={definition.Clsid}, HandlerPath={definition.ComServer.Path ?? "<none>"}, IsEnabled={blockedState.IsEnabled}, BlockedSource={blockedState.Source}, LogoPath=<not-resolved>.");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogFireAndForget(RuntimeLogLevel.Warning, $"Win11ContextMenuProjectionFailure: PackageFullName={definition.Package.FullName}, Clsid={definition.Clsid}, Exception={ex}");
            }
        }

        var result = items.Values
            .OrderBy(static item => item.Category)
            .ThenBy(static item => item.Windows11SourceKind)
            .ThenBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _logger?.LogFireAndForget($"Win11ContextMenuEnumerateSummary: IsSupported={IsSupported}, UserSid={userSid}, PackageCount={definitions.Select(static definition => definition.Package.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count()}, PackagedDefinitionCount={definitions.Count}, EntriesCount={result.Length}, SystemCommandCount={result.Count(static item => item.Windows11SourceKind == Windows11ContextMenuSourceKind.SystemCommandStore)}, BlockedMachineCount={GetMachineBlockedCount()}, BlockedUserCount={GetUserBlockedCount(userSid)}.");
        return result;
    }

    /// <summary>
    /// Sets enabled for a Windows 11 system CommandStore item using shell verb visibility only.
    /// </summary>
    public async Task<PipeResponse> SetSystemCommandEnabledAsync(
        string commandKey,
        bool enable,
        Guid? operationId,
        CancellationToken cancellationToken)
    {
        var registryPath = $@"HKEY_LOCAL_MACHINE\{SystemCommandStorePath}\{commandKey}";
        if (string.IsNullOrWhiteSpace(commandKey) || !IsSupportedSystemCommandKey(commandKey))
        {
            return Failure("Only Windows 11 system CommandStore command keys can be modified here.", operationId);
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{SystemCommandStorePath}\{commandKey}", writable: true);
            if (key is null)
            {
                return Failure("The Windows 11 system command was not found in CommandStore.", operationId);
            }

            ShellVerbVisibility.SetEnabled(key, registryPath, enable);
            if (_logger is not null)
            {
                await _logger.LogAsync(
                    DiagnosticLogFormatter.BuildRegistryOperationLog(
                        "Win11SystemCommandSetEnabled",
                        registryPath,
                        commandKey,
                        null,
                        null,
                        writable: true,
                        result: $"Success, Enable={enable}"),
                    cancellationToken);
            }

            return new PipeResponse
            {
                Success = true,
                Message = enable
                    ? "Windows 11 system command enabled successfully."
                    : "Windows 11 system command disabled successfully.",
                ClientOperationId = operationId
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            if (_logger is not null)
            {
                await _logger.LogAsync(RuntimeLogLevel.Warning, $"Win11SystemCommandProtected: Path={registryPath}, CommandKey={commandKey}, Exception={ex}", cancellationToken);
            }

            return ProtectedFailure(operationId);
        }
        catch (System.Security.SecurityException ex)
        {
            if (_logger is not null)
            {
                await _logger.LogAsync(RuntimeLogLevel.Warning, $"Win11SystemCommandProtected: Path={registryPath}, CommandKey={commandKey}, Exception={ex}", cancellationToken);
            }

            return ProtectedFailure(operationId);
        }
    }

    /// <summary>
    /// Sets enabled.
    /// </summary>
    public bool SetEnabled(string handlerClsid, string displayName, BackendUserContext? userContext, bool enable)
    {
        var normalizedClsid = NormalizeGuid(handlerClsid);
        var userSid = userContext?.Sid;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            // 不应该发生，但作为安全措施
            userSid = TryGetBestInteractiveUserSid();
        }

        if (!IsSupported || string.IsNullOrWhiteSpace(userSid) || string.IsNullOrWhiteSpace(normalizedClsid))
        {
            return false;
        }

        using var userRoot = Registry.Users.CreateSubKey($@"{userSid}\{UserBlockedPathSuffix}", writable: true);
        if (userRoot is null)
        {
            return false;
        }

        if (enable)
        {
            DeleteGuidValue(userRoot, normalizedClsid);
            _logger?.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("Win11ContextMenuSetEnabled", $@"HKEY_USERS\{userSid}\{UserBlockedPathSuffix}", normalizedClsid, RegistryValueKind.String, null, writable: true, result: "DeleteValue Success, Enable=true"));
        }
        else
        {
            userRoot.SetValue(normalizedClsid, displayName, RegistryValueKind.String);
            _logger?.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("Win11ContextMenuSetEnabled", $@"HKEY_USERS\{userSid}\{UserBlockedPathSuffix}", normalizedClsid, RegistryValueKind.String, displayName, writable: true, result: "SetValue Success, Enable=false"));
        }

        return true;
    }

    /// <summary>
    /// Gets is Enabled.
    /// </summary>
    public bool GetIsEnabled(string handlerClsid, BackendUserContext? userContext)
    {
        var userSid = userContext?.Sid;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            // 不应该发生，但作为安全措施
            userSid = TryGetBestInteractiveUserSid();
        }

        return GetBlockedState(handlerClsid, userSid).IsEnabled;
    }

    internal static ContextMenuEntry CreateEntry(
        PackagedContextMenuDefinition definition,
        ContextMenuCategory category,
        Windows11BlockedState blockedState,
        string userSid)
    {
        var normalizedClsid = NormalizeGuid(definition.Clsid);
        var registryPath = $@"PackagedCom\Package\{definition.Package.FullName}\Class\{normalizedClsid}";
        var blockedPath = $@"HKEY_USERS\{userSid}\{UserBlockedPathSuffix}";
        var contextTypesText = string.Join(", ", definition.ContextTypes);
        var notes = string.IsNullOrWhiteSpace(contextTypesText)
            ? $"Win11 packaged context menu from {definition.Package.DisplayName}"
            : $"Win11 packaged context menu. Context types: {contextTypesText}";

        return new ContextMenuEntry
        {
            Id = $"win11|{normalizedClsid}|{category}",
            Category = category,
            EntryKind = ContextMenuEntryKind.ShellExtension,
            KeyName = normalizedClsid,
            DisplayName = definition.DisplayName,
            EditableText = null,
            RegistryPath = registryPath,
            BackendRegistryPath = blockedPath,
            SourceRootPath = ContextMenuRegistryCatalog.Windows11MonitoredRootPath,
            CommandText = null,
            HandlerClsid = normalizedClsid,
            IconPath = null,
            IconIndex = 0,
            FilePath = definition.ComServer.Path,
            IsWindows11ContextMenu = true,
            Windows11SourceKind = Windows11ContextMenuSourceKind.PackagedCom,
            Windows11PackageFullName = definition.Package.FullName,
            Windows11PackageFamilyName = definition.Package.FamilyName,
            Windows11PackageDisplayName = definition.Package.DisplayName,
            Windows11PackagePublisherDisplayName = definition.Package.PublisherDisplayName,
            Windows11PackageInstallPath = definition.Package.InstallPath,
            Windows11ContextTypes = definition.ContextTypes,
            Windows11Verbs = definition.Verbs
                .Select(static verb => new Windows11ContextMenuVerbMetadata
                {
                    Id = verb.Id,
                    HandlerClsid = verb.Clsid,
                    ContextType = verb.ContextType
                })
                .ToArray(),
            Windows11ComServerDisplayName = definition.ComServer.ServerDisplayName,
            Windows11ComClassDisplayName = definition.ComServer.ClassDisplayName,
            IsMachineBlocked = blockedState.IsMachineBlocked,
            IsEnabled = blockedState.IsEnabled,
            IsPresentInRegistry = true,
            Notes = notes
        };
    }

    internal static IEnumerable<ContextMenuCategory> MapCategories(IReadOnlyList<string> contextTypes)
    {
        var categories = new HashSet<ContextMenuCategory>();
        foreach (var rawType in contextTypes)
        {
            if (string.IsNullOrWhiteSpace(rawType))
            {
                continue;
            }

            var type = rawType.Trim();
            if (type.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
            {
                var fileType = type["File:".Length..].Trim();
                if (string.Equals(fileType, "Directory\\Background", StringComparison.OrdinalIgnoreCase))
                {
                    categories.Add(ContextMenuCategory.DirectoryBackground);
                }
                else if (string.Equals(fileType, "DesktopBackground", StringComparison.OrdinalIgnoreCase))
                {
                    categories.Add(ContextMenuCategory.DesktopBackground);
                }
                else if (string.Equals(fileType, "Drive", StringComparison.OrdinalIgnoreCase))
                {
                    categories.Add(ContextMenuCategory.Drive);
                }
                else if (string.Equals(fileType, "Folder", StringComparison.OrdinalIgnoreCase))
                {
                    categories.Add(ContextMenuCategory.Folder);
                }
                else if (string.Equals(fileType, "AllFileSystemObjects", StringComparison.OrdinalIgnoreCase))
                {
                    categories.Add(ContextMenuCategory.AllFileSystemObjects);
                }
                else if (string.Equals(fileType, "LibraryFolder", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(fileType, "LibraryFolder\\Background", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(fileType, "UserLibraryFolder", StringComparison.OrdinalIgnoreCase))
                {
                    categories.Add(ContextMenuCategory.Library);
                }
                else if (string.Equals(fileType, "CLSID\\{20D04FE0-3AEA-1069-A2D8-08002B30309D}", StringComparison.OrdinalIgnoreCase))
                {
                    categories.Add(ContextMenuCategory.Computer);
                }
                else if (string.Equals(fileType, "CLSID\\{645FF040-5081-101B-9F08-00AA002F954E}", StringComparison.OrdinalIgnoreCase))
                {
                    categories.Add(ContextMenuCategory.RecycleBin);
                }
                else
                {
                    categories.Add(ContextMenuCategory.File);
                }

                continue;
            }

            if (string.Equals(type, "Directory", StringComparison.OrdinalIgnoreCase))
            {
                categories.Add(ContextMenuCategory.Directory);
                continue;
            }

            categories.Add(ContextMenuCategory.File);
        }

        if (categories.Count == 0)
        {
            categories.Add(ContextMenuCategory.File);
        }

        return categories;
    }

    private static string NormalizeGuid(string guidText)
    {
        return Guid.TryParse(guidText, out var guid)
            ? guid.ToString("B")
            : guidText.Trim();
    }

    private static bool HasGuidValue(RegistryKey? key, string normalizedClsid)
    {
        if (key is null)
        {
            return false;
        }

        if (key.GetValue(normalizedClsid) is not null)
        {
            return true;
        }

        // ContextMenuMgr writes {GUID} values; manual registry edits may omit braces.
        return key.GetValueNames()
            .Any(valueName => string.Equals(NormalizeGuid(valueName), normalizedClsid, StringComparison.OrdinalIgnoreCase));
    }

    private static Windows11BlockedState GetBlockedState(string handlerClsid, string? userSid)
    {
        var normalizedClsid = NormalizeGuid(handlerClsid);
        if (string.IsNullOrWhiteSpace(normalizedClsid))
        {
            return CreateBlockedState(machineBlocked: false, userBlocked: false);
        }

        using var machineBlocked = Registry.LocalMachine.OpenSubKey(MachineBlockedPath, writable: false);
        var machine = HasGuidValue(machineBlocked, normalizedClsid);
        var user = false;
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            using var userBlocked = Registry.Users.OpenSubKey($@"{userSid}\{UserBlockedPathSuffix}", writable: false);
            user = HasGuidValue(userBlocked, normalizedClsid);
        }

        return CreateBlockedState(machine, user);
    }

    internal static Windows11BlockedState CreateBlockedState(bool machineBlocked, bool userBlocked) =>
        new(machineBlocked, userBlocked);

    private static int GetMachineBlockedCount()
    {
        using var key = Registry.LocalMachine.OpenSubKey(MachineBlockedPath, writable: false);
        return key?.GetValueNames().Length ?? 0;
    }

    private static int GetUserBlockedCount(string userSid)
    {
        using var key = Registry.Users.OpenSubKey($@"{userSid}\{UserBlockedPathSuffix}", writable: false);
        return key?.GetValueNames().Length ?? 0;
    }

    private static void DeleteGuidValue(RegistryKey key, string normalizedClsid)
    {
        key.DeleteValue(normalizedClsid, throwOnMissingValue: false);

        foreach (var valueName in key.GetValueNames())
        {
            if (string.Equals(NormalizeGuid(valueName), normalizedClsid, StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
    }

    private static string? TryGetBestInteractiveUserSid()
    {
        var consoleSessionId = unchecked((int)NativeMethods.WTSGetActiveConsoleSessionId());
        if (consoleSessionId != -1 && TryGetUserSid(consoleSessionId, out var consoleSid))
        {
            return consoleSid;
        }

        if (!NativeMethods.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessionInfoPtr, out var count))
        {
            return null;
        }

        try
        {
            var dataSize = Marshal.SizeOf<NativeMethods.WTS_SESSION_INFO>();
            string? connectedSid = null;

            for (var index = 0; index < count; index++)
            {
                var current = IntPtr.Add(sessionInfoPtr, index * dataSize);
                var sessionInfo = Marshal.PtrToStructure<NativeMethods.WTS_SESSION_INFO>(current);
                if (sessionInfo.SessionID == -1)
                {
                    continue;
                }

                if (!TryGetUserSid(sessionInfo.SessionID, out var userSid))
                {
                    continue;
                }

                if (sessionInfo.State == NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive)
                {
                    return userSid;
                }

                if (connectedSid is null && sessionInfo.State == NativeMethods.WTS_CONNECTSTATE_CLASS.WTSConnected)
                {
                    connectedSid = userSid;
                }
            }

            return connectedSid;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(sessionInfoPtr);
        }
    }

    private IEnumerable<ContextMenuEntry> EnumerateSystemCommandStoreEntries()
    {
        using var root = Registry.LocalMachine.OpenSubKey(SystemCommandStorePath, writable: false);
        if (root is null)
        {
            return [];
        }

        var entries = new List<ContextMenuEntry>();
        foreach (var name in root.GetSubKeyNames().Where(IsSupportedSystemCommandKey))
        {
            using var itemKey = root.OpenSubKey(name, writable: false);
            if (itemKey is null || !HasSystemCommandMetadata(itemKey))
            {
                continue;
            }

            entries.Add(CreateSystemCommandEntry(name, itemKey));
        }

        return entries;
    }

    private ContextMenuEntry CreateSystemCommandEntry(string commandKey, RegistryKey itemKey)
    {
        var registryPath = $@"HKEY_LOCAL_MACHINE\{SystemCommandStorePath}\{commandKey}";
        var commandText = itemKey.OpenSubKey("command", writable: false)?.GetValue(null)?.ToString();
        var handlerGuid = ResolveSystemCommandHandlerGuid(itemKey);
        var icon = ShellMetadataResolver.ResolveVerbIcon(itemKey, commandText);
        var filePath = ShellMetadataResolver.ResolveVerbFilePath(itemKey, commandText)
            ?? ShellMetadataResolver.ResolveShellExtensionFilePath(handlerGuid);
        var displayName = ShellMetadataResolver.ResolveVerbDisplayName(itemKey, commandKey);
        var isEnabled = ShellVerbVisibility.IsEnabled(itemKey);
        var isProtected = IsSystemCommandProtected(commandKey);
        var notes = "System Command / CommandStore. Do not use GUID Lock for Windows 11 built-in commands. Blocking the wrong Explorer command handler may cause the Windows 11 modern context menu to fall back to the classic menu.";

        _logger?.LogFireAndForget($"Win11SystemCommandEntry: CommandKey={commandKey}, DisplayName={displayName}, HandlerGuid={handlerGuid ?? "<none>"}, IsEnabled={isEnabled}, IsProtected={isProtected}.");
        return new ContextMenuEntry
        {
            Id = $"win11-system|{commandKey}",
            Category = ContextMenuCategory.File,
            EntryKind = ContextMenuEntryKind.ShellVerb,
            KeyName = commandKey,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? commandKey : displayName,
            RegistryPath = registryPath,
            BackendRegistryPath = registryPath,
            SourceRootPath = @"CommandStore\shell",
            CommandText = commandText,
            HandlerClsid = handlerGuid,
            IconPath = icon.IconPath,
            IconIndex = icon.IconIndex,
            FilePath = filePath,
            IsWindows11ContextMenu = true,
            Windows11SourceKind = Windows11ContextMenuSourceKind.SystemCommandStore,
            IsProtectedSystemItem = isProtected,
            IsEnabled = isEnabled,
            IsPresentInRegistry = true,
            Notes = notes
        };
    }

    private static bool IsSupportedSystemCommandKey(string keyName) =>
        keyName.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase)
        && SupportedSystemCommandKeys.Contains(keyName);

    private static bool HasSystemCommandMetadata(RegistryKey itemKey)
    {
        foreach (var valueName in new[]
                 {
                     "MUIVerb",
                     "ExplorerCommandHandler",
                     "DelegateExecute",
                     "CommandFlags",
                     "AppliesTo",
                     "Icon",
                     "ImpliedSelectionModel"
                 })
        {
            if (itemKey.GetValue(valueName) is not null)
            {
                return true;
            }
        }

        return itemKey.OpenSubKey("command", writable: false) is not null;
    }

    private static string? ResolveSystemCommandHandlerGuid(RegistryKey itemKey)
    {
        foreach (var valueName in new[] { "ExplorerCommandHandler", "DelegateExecute" })
        {
            var value = itemKey.GetValue(valueName)?.ToString();
            if (Guid.TryParse(value, out var guid))
            {
                return guid.ToString("B");
            }
        }

        return null;
    }

    private static PipeResponse ProtectedFailure(Guid? operationId) => new()
    {
        Success = false,
        ErrorCode = "WIN11_SYSTEM_COMMAND_PROTECTED",
        Message = "This Windows 11 system command is protected by Windows and cannot be safely modified by ContextMenuMgr.",
        ClientOperationId = operationId
    };

    private static PipeResponse Failure(string message, Guid? operationId) => new()
    {
        Success = false,
        Message = message,
        ClientOperationId = operationId
    };

    private static bool IsSystemCommandProtected(string commandKey)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{SystemCommandStorePath}\{commandKey}", writable: true);
            return key is null;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (System.Security.SecurityException)
        {
            return true;
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
            int reserved,
            int version,
            out IntPtr ppSessionInfo,
            out int pCount);

        /// <summary>
        /// Executes wTS Free Memory.
        /// </summary>
        [DllImport("wtsapi32.dll")]
        public static extern void WTSFreeMemory(IntPtr memory);

        /// <summary>
        /// Executes close Handle.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

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
            public string pWinStationName;
            public WTS_CONNECTSTATE_CLASS State;
        }
    }

}

internal readonly record struct Windows11BlockedState(bool IsMachineBlocked, bool IsUserBlocked)
{
    public bool IsEnabled => !IsMachineBlocked && !IsUserBlocked;

    public string Source => (IsMachineBlocked, IsUserBlocked) switch
    {
        (true, true) => "Both",
        (true, false) => "Machine",
        (false, true) => "User",
        _ => "None"
    };
}
