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
    private static readonly HashSet<string> SupportedSystemCommandKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows.SendToMyPhone",
        "Windows.Share"
    };

    private readonly FileLogger? _logger;
    private readonly PackagedContextMenuScanCache _packageScanCache;
    private readonly Dictionary<string, (DateTimeOffset CapturedAt, IReadOnlyList<ContextMenuEntry> Entries)> _mutationDiscovery = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _discoveryLock = new();

    public Windows11ContextMenuCatalog(FileLogger? logger = null)
        : this(logger, null)
    {
    }

    internal Windows11ContextMenuCatalog(FileLogger? logger, PackagedContextMenuScanCache? packageScanCache)
    {
        _logger = logger;
        _packageScanCache = packageScanCache ?? new PackagedContextMenuScanCache(
            sid => PackagedContextMenuDiscovery.FindForUser(sid, _logger, CancellationToken.None));
    }

    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    // Manual changes reuse recent discovery, never cached switch values. Normal
    // snapshots still rebuild the impact index for automatic policy decisions.
    public async Task<IReadOnlyList<ContextMenuEntry>> EnumerateForMutationAsync(CancellationToken cancellationToken, BackendUserContext userContext)
    {
        IReadOnlyList<ContextMenuEntry>? recent = null;
        lock (_discoveryLock)
        {
            if (_mutationDiscovery.TryGetValue(userContext.Sid, out var cached)
                && DateTimeOffset.UtcNow - cached.CapturedAt < TimeSpan.FromSeconds(30))
                recent = cached.Entries;
        }
        return recent is null
            ? await EnumerateEntriesAsync(cancellationToken, userContext)
            : RefreshStates(recent, userContext.Sid, cancellationToken);
    }

    public IReadOnlyList<ContextMenuEntry> RefreshStates(IEnumerable<ContextMenuEntry> entries, string userSid, CancellationToken cancellationToken)
    {
        var blocked = new Dictionary<string, Windows11BlockedState>(StringComparer.OrdinalIgnoreCase);
        var commands = EnumerateSystemCommandStoreEntries().ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<ContextMenuEntry>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Windows11SourceKind == Windows11ContextMenuSourceKind.SystemCommandStore)
            {
                if (commands.TryGetValue(entry.Id, out var command)) result.Add(command);
                continue;
            }
            var clsid = entry.HandlerClsid!;
            if (!blocked.TryGetValue(clsid, out var state))
                blocked[clsid] = state = Windows11BlocksService.ReadState(clsid, userSid);
            result.Add(entry with { IsEnabled = state.IsEnabled, IsMachineBlocked = state.IsMachineBlocked });
        }
        return result;
    }

    /// <summary>
    /// Executes enumerate Entries Async.
    /// </summary>
    public async Task<IReadOnlyList<ContextMenuEntry>> EnumerateEntriesAsync(CancellationToken cancellationToken, BackendUserContext? userContext = null)
    {
        var userSid = userContext?.Sid;
        if (!IsSupported || string.IsNullOrWhiteSpace(userSid))
        {
            _logger?.LogFireAndForget($"Win11ContextMenuEnumerate: IsSupported={IsSupported}, UserSid={userSid ?? "<null>"}, Result=Skipped.");
            return [];
        }

        var items = new Dictionary<string, ContextMenuEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var commandEntry in EnumerateSystemCommandStoreEntries())
        {
            items[commandEntry.Id] = commandEntry;
        }

        var definitions = await _packageScanCache.GetAsync(userSid, cancellationToken);
        var impactIndex = new Windows11ClsidImpactIndex(definitions);
        if (definitions.Count > 0) impactIndex.ReadRegistry(userSid, cancellationToken);
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockedState = Windows11BlocksService.ReadState(definition.Clsid, userSid);
            foreach (var category in MapCategories(definition.ContextTypes))
            {
                var entry = CreateEntry(definition, category, blockedState, userSid) with
                { Windows11ClsidImpact = impactIndex.Get(definition.Clsid) };
                // Family identity survives package updates. Coexisting versions of
                // that family retain every full-name declaration in the impact index.
                items.TryAdd(entry.Id, entry);
            }
        }

        var result = items.Values
            .OrderBy(static item => item.Category)
            .ThenBy(static item => item.Windows11SourceKind)
            .ThenBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _logger?.LogFireAndForget($"Win11ContextMenuEnumerateSummary: IsSupported={IsSupported}, UserSid={userSid}, PackageCount={definitions.Select(static definition => definition.Package.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count()}, PackagedDefinitionCount={definitions.Count}, EntriesCount={result.Length}, SystemCommandCount={result.Count(static item => item.Windows11SourceKind == Windows11ContextMenuSourceKind.SystemCommandStore)}.");
        lock (_discoveryLock) _mutationDiscovery[userSid] = (DateTimeOffset.UtcNow, result);
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
            Id = $"win11|{normalizedClsid}|{category}|{definition.Package.FamilyName}",
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

    // v1.7.10 and earlier keyed declarations only by CLSID/category. Preserve the
    // known package's policy. Previously hidden colliding sources adopt physical
    // state, without inventing an approval or ownership of a blocked value.
    internal static bool MigrateStateIdentities(
        IDictionary<string, PersistedContextMenuState> states, IEnumerable<ContextMenuEntry> entries)
    {
        var changed = false;
        foreach (var group in entries.Where(static entry => entry.IsWindows11ContextMenu
                     && entry.Windows11SourceKind == Windows11ContextMenuSourceKind.PackagedCom)
                     .GroupBy(static entry => $"win11|{entry.HandlerClsid}|{entry.Category}", StringComparer.OrdinalIgnoreCase))
        {
            if (!states.TryGetValue(group.Key, out var previous) || !previous.IsWindows11ContextMenu || previous.IsDeleted) continue;
            var oldPackage = ContextMenuApprovalIdentity.ExtractWin11PackageKey(previous.RegistryPath).Split('_');
            var oldFamily = oldPackage.Length >= 5 ? oldPackage[0] + "_" + oldPackage[^1] : string.Empty;
            foreach (var entry in group)
            {
                if (states.ContainsKey(entry.Id)) continue;
                var state = string.Equals(entry.Windows11PackageFamilyName, oldFamily, StringComparison.OrdinalIgnoreCase)
                    ? previous : PersistedContextMenuState.FromEntry(entry);
                state.Id = entry.Id;
                states[entry.Id] = state;
            }
            states.Remove(group.Key);
            changed = true;
        }
        return changed;
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
            CanToggle = !isProtected,
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
