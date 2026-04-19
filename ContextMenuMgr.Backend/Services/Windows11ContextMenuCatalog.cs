using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Represents the windows11 Context Menu Catalog.
/// </summary>
internal sealed class Windows11ContextMenuCatalog
{
    private const string PackagedComPath = @"PackagedCom\Package";
    private const string PackageRepositoryPath = @"Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PackageRepository\Packages";
    private const string UserBlockedPathSuffix = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string MachineBlockedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string NamespaceCom = "http://schemas.microsoft.com/appx/manifest/com/windows10";
    private const string NamespaceDesktop4 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4";

    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>
    /// Executes enumerate Entries Async.
    /// </summary>
    public async Task<IReadOnlyList<ContextMenuEntry>> EnumerateEntriesAsync(CancellationToken cancellationToken, string? userSid = null)
    {
        userSid ??= TryGetBestInteractiveUserSid();
        if (!IsSupported || string.IsNullOrWhiteSpace(userSid))
        {
            return [];
        }

        var packageNames = GetPackagedComPackages();
        if (packageNames.Length == 0)
        {
            return [];
        }

        var items = new ConcurrentDictionary<string, ContextMenuEntry>(StringComparer.OrdinalIgnoreCase);

        await Parallel.ForEachAsync(
            packageNames,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken
            },
            async (fullName, ct) =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var package = TryGetPackageInfo(fullName);
                    if (package is null)
                    {
                        return;
                    }

                    var definitions = await AnalyzeManifestAsync(
                        package,
                        ct);

                    foreach (var definition in definitions)
                    {
                        var isEnabled = GetIsEnabled(definition.Id, userSid);
                        foreach (var category in MapCategories(definition.ContextTypes))
                        {
                            var entry = CreateEntry(definition, category, isEnabled, userSid);
                            items[entry.Id] = entry;
                        }
                    }
                }
                catch
                {
                }
            });

        return items.Values
            .OrderBy(static item => item.Category)
            .ThenBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Sets enabled.
    /// </summary>
    public bool SetEnabled(string handlerClsid, string displayName, string? userSid, bool enable)
    {
        userSid ??= TryGetBestInteractiveUserSid();
        if (!IsSupported || string.IsNullOrWhiteSpace(userSid) || string.IsNullOrWhiteSpace(handlerClsid))
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
            userRoot.DeleteValue(handlerClsid, throwOnMissingValue: false);
        }
        else
        {
            userRoot.SetValue(handlerClsid, displayName, RegistryValueKind.String);
        }

        return true;
    }

    /// <summary>
    /// Gets is Enabled.
    /// </summary>
    public bool GetIsEnabled(string handlerClsid, string? userSid)
    {
        if (string.IsNullOrWhiteSpace(handlerClsid))
        {
            return true;
        }

        using var machineBlocked = Registry.LocalMachine.OpenSubKey(MachineBlockedPath, writable: false);
        if (machineBlocked?.GetValue(handlerClsid) is not null)
        {
            return false;
        }

        userSid ??= TryGetBestInteractiveUserSid();
        if (string.IsNullOrWhiteSpace(userSid))
        {
            return true;
        }

        using var userBlocked = Registry.Users.OpenSubKey($@"{userSid}\{UserBlockedPathSuffix}", writable: false);
        return userBlocked?.GetValue(handlerClsid) is null;
    }

    private static string[] GetPackagedComPackages()
    {
        using var subKey = Registry.ClassesRoot.OpenSubKey(PackagedComPath, writable: false);
        return subKey?.GetSubKeyNames() ?? [];
    }

    private static Windows11PackageInfo? TryGetPackageInfo(string packageFullName)
    {
        using var packageInfoKey = Registry.ClassesRoot.OpenSubKey($@"{PackageRepositoryPath}\{packageFullName}", writable: false);
        var installPath = packageInfoKey?.GetValue("Path")?.ToString();
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return null;
        }

        return new Windows11PackageInfo(
            FamilyName: packageFullName.Split('_')[0],
            FullName: packageFullName,
            DisplayName: packageFullName,
            PublisherDisplayName: packageFullName,
            InstallPath: installPath,
            Version: Version.TryParse(packageInfoKey?.GetValue("Version")?.ToString(), out var version)
                ? version
                : new Version(0, 0));
    }

    private static async Task<IReadOnlyList<Windows11ContextMenuItemDefinition>> AnalyzeManifestAsync(
        Windows11PackageInfo package,
        CancellationToken cancellationToken)
    {
        var manifestPath = File.Exists(Path.Combine(package.InstallPath, "AppxManifest.xml"))
            ? Path.Combine(package.InstallPath, "AppxManifest.xml")
            : Path.Combine(package.InstallPath, @"AppxMetadata\AppxBundleManifest.xml");

        if (!File.Exists(manifestPath))
        {
            return [];
        }

        await using var stream = File.OpenRead(manifestPath);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore
        });

        var nsResolver = (IXmlNamespaceResolver)reader;
        if (!reader.ReadToFollowing("Package")
            || nsResolver.LookupPrefix(NamespaceDesktop4) is null
            || nsResolver.LookupPrefix(NamespaceCom) is null)
        {
            return [];
        }

        var contextMenus = new Dictionary<string, List<Windows11ContextMenuVerb>>(StringComparer.OrdinalIgnoreCase);
        var comServers = new Dictionary<string, Windows11ComServerInfo>(StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "FileExplorerContextMenus":
                {
                    var element = (XElement)XNode.ReadFrom(reader);
                    var query =
                        from itemType in element.Elements()
                        where itemType.Name.LocalName == "ItemType"
                        from verb in itemType.Elements()
                        where verb.Name.LocalName == "Verb"
                        let type = itemType.Attribute("Type")?.Value
                        let item = new Windows11ContextMenuVerb(
                            verb.Attribute("Clsid")?.Value,
                            verb.Attribute("Id")?.Value,
                            string.Equals(type, "Directory", StringComparison.OrdinalIgnoreCase)
                                ? type
                                : $"File: {type}")
                        group item by item.Clsid;

                    foreach (var group in query)
                    {
                        if (!string.IsNullOrWhiteSpace(group.Key))
                        {
                            contextMenus[group.Key] = group.ToList();
                        }
                    }

                    break;
                }
                case "ComServer":
                {
                    var element = (XElement)XNode.ReadFrom(reader);
                    var query =
                        from server in element.Elements()
                        where server.Name.LocalName is "SurrogateServer" or "ExeServer"
                        from cls in server.Elements()
                        where cls.Name.LocalName == "Class"
                        let item = new Windows11ComServerInfo(
                            cls.Attribute("Id")?.Value,
                            Path.Combine(
                                package.InstallPath,
                                cls.Attribute("Path")?.Value ?? server.Attribute("Executable")?.Value ?? string.Empty),
                            server.Attribute("DisplayName")?.Value)
                        group item by item.Id;

                    foreach (var group in query)
                    {
                        if (!string.IsNullOrWhiteSpace(group.Key))
                        {
                            comServers[group.Key] = group.First();
                        }
                    }

                    break;
                }
            }
        }

        return contextMenus.Keys
            .Intersect(comServers.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(id =>
            {
                var comServer = comServers[id];
                var displayName = string.IsNullOrWhiteSpace(comServer.DisplayName)
                    ? package.DisplayName
                    : comServer.DisplayName;
                var contextTypes = contextMenus[id]
                    .Select(static item => item.Type)
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new Windows11ContextMenuItemDefinition(
                    id,
                    displayName ?? package.DisplayName,
                    package,
                    contextMenus[id],
                    comServer,
                    contextTypes);
            })
            .ToArray();
    }

    private static ContextMenuEntry CreateEntry(
        Windows11ContextMenuItemDefinition definition,
        ContextMenuCategory category,
        bool isEnabled,
        string userSid)
    {
        var normalizedClsid = NormalizeGuid(definition.Id);
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
            FilePath = File.Exists(definition.ComServer.Path ?? string.Empty)
                ? definition.ComServer.Path
                : definition.Package.InstallPath,
            IsWindows11ContextMenu = true,
            IsEnabled = isEnabled,
            IsPresentInRegistry = true,
            Notes = notes
        };
    }

    private static IEnumerable<ContextMenuCategory> MapCategories(IReadOnlyList<string> contextTypes)
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

    private sealed record Windows11PackageInfo(
        string FamilyName,
        string FullName,
        string DisplayName,
        string PublisherDisplayName,
        string InstallPath,
        Version Version);

    private sealed record Windows11ContextMenuVerb(
        string? Clsid,
        string? Id,
        string? Type);

    private sealed record Windows11ComServerInfo(
        string? Id,
        string? Path,
        string? DisplayName);

    private sealed record Windows11ContextMenuItemDefinition(
        string Id,
        string DisplayName,
        Windows11PackageInfo Package,
        IReadOnlyList<Windows11ContextMenuVerb> ContextMenus,
        Windows11ComServerInfo ComServer,
        IReadOnlyList<string> ContextTypes);
}
