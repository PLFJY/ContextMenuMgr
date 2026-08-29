using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.IO;
using System.Runtime.InteropServices;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Represents the context Menu Registry Catalog.
/// </summary>
public sealed class ContextMenuRegistryCatalog
{
    internal const string Windows11MonitoredRootPath = @"PackagedCom\Windows11ContextMenu";
    internal const string RegularBaselineMarkerId = "internal:baseline:regular:v1";
    internal const string WpsOfficeBaselineMarkerId = "internal:baseline:wps-office:v1";
    internal const string WpsOfficeDocumentIconSyntheticId = "special:wps-office-icon:document-icons";
    private const string BaselineMarkerSourceRootPath = "internal:baseline";
    private const string RecycleBinPinToHomeId = "special:recyclebin:pintohome";
    private const string RecycleBinPinToHomeRegistryPath = @"HKEY_CLASSES_ROOT\Folder\shell\pintohome";
    private const string RecycleBinPinToHomeSourceRootPath = @"Folder\shell";
    private const string RecycleBinParsingNameExclusion = @"System.ParsingName:<>""::{645FF040-5081-101B-9F08-00AA002F954E}""";
    private const string LegacyGlobalShellExtensionsBlockedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private static readonly string[] ContextMenuSubRootRelativePaths =
    [
        "shell",
        @"shellex\ContextMenuHandlers",
        @"shellex\-ContextMenuHandlers"
    ];

    private static readonly RegistryRootDescriptor[] MonitoredRoots =
    [
        new(ContextMenuCategory.AllFileSystemObjects, @"AllFilesystemObjects\shell", ContextMenuEntryKind.ShellVerb),
        new(ContextMenuCategory.AllFileSystemObjects, @"AllFilesystemObjects\shellex\ContextMenuHandlers", ContextMenuEntryKind.ShellExtension),
        new(ContextMenuCategory.AllFileSystemObjects, @"AllFilesystemObjects\shellex\-ContextMenuHandlers", ContextMenuEntryKind.ShellExtension, @"AllFilesystemObjects\shellex\ContextMenuHandlers", true),
        new(ContextMenuCategory.File, @"*\shell", ContextMenuEntryKind.ShellVerb),
        new(ContextMenuCategory.File, @"*\shellex\ContextMenuHandlers", ContextMenuEntryKind.ShellExtension),
        new(ContextMenuCategory.File, @"*\shellex\-ContextMenuHandlers", ContextMenuEntryKind.ShellExtension, @"*\shellex\ContextMenuHandlers", true),
        new(ContextMenuCategory.Folder, @"Folder\shell", ContextMenuEntryKind.ShellVerb),
        new(ContextMenuCategory.Folder, @"Folder\shellex\ContextMenuHandlers", ContextMenuEntryKind.ShellExtension),
        new(ContextMenuCategory.Folder, @"Folder\shellex\-ContextMenuHandlers", ContextMenuEntryKind.ShellExtension, @"Folder\shellex\ContextMenuHandlers", true),
        new(ContextMenuCategory.Directory, @"Directory\shell", ContextMenuEntryKind.ShellVerb),
        new(ContextMenuCategory.Directory, @"Directory\shellex\ContextMenuHandlers", ContextMenuEntryKind.ShellExtension),
        new(ContextMenuCategory.Directory, @"Directory\shellex\-ContextMenuHandlers", ContextMenuEntryKind.ShellExtension, @"Directory\shellex\ContextMenuHandlers", true),
        new(ContextMenuCategory.DirectoryBackground, @"Directory\Background\shell", ContextMenuEntryKind.ShellVerb),
        new(ContextMenuCategory.DirectoryBackground, @"Directory\Background\shellex\ContextMenuHandlers", ContextMenuEntryKind.ShellExtension),
        new(ContextMenuCategory.DirectoryBackground, @"Directory\Background\shellex\-ContextMenuHandlers", ContextMenuEntryKind.ShellExtension, @"Directory\Background\shellex\ContextMenuHandlers", true),
        new(ContextMenuCategory.DesktopBackground, @"DesktopBackground\shell", ContextMenuEntryKind.ShellVerb),
        new(ContextMenuCategory.DesktopBackground, @"DesktopBackground\shellex\ContextMenuHandlers", ContextMenuEntryKind.ShellExtension),
        new(ContextMenuCategory.DesktopBackground, @"DesktopBackground\shellex\-ContextMenuHandlers", ContextMenuEntryKind.ShellExtension, @"DesktopBackground\shellex\ContextMenuHandlers", true),
        new(ContextMenuCategory.Drive, @"Drive\shell", ContextMenuEntryKind.ShellVerb),
        new(ContextMenuCategory.Drive, @"Drive\shellex\ContextMenuHandlers", ContextMenuEntryKind.ShellExtension),
        new(ContextMenuCategory.Drive, @"Drive\shellex\-ContextMenuHandlers", ContextMenuEntryKind.ShellExtension, @"Drive\shellex\ContextMenuHandlers", true),
        new(ContextMenuCategory.Library, @"LibraryFolder\shell", ContextMenuEntryKind.ShellVerb),
        new(ContextMenuCategory.Library, @"LibraryFolder\shellex\ContextMenuHandlers", ContextMenuEntryKind.ShellExtension),
        new(ContextMenuCategory.Library, @"LibraryFolder\shellex\-ContextMenuHandlers", ContextMenuEntryKind.ShellExtension, @"LibraryFolder\shellex\ContextMenuHandlers", true),
        new(ContextMenuCategory.Library, @"LibraryFolder\Background\shell", ContextMenuEntryKind.ShellVerb),
        new(ContextMenuCategory.Library, @"LibraryFolder\Background\shellex\ContextMenuHandlers", ContextMenuEntryKind.ShellExtension),
        new(ContextMenuCategory.Library, @"LibraryFolder\Background\shellex\-ContextMenuHandlers", ContextMenuEntryKind.ShellExtension, @"LibraryFolder\Background\shellex\ContextMenuHandlers", true),
        new(ContextMenuCategory.Library, @"UserLibraryFolder\shell", ContextMenuEntryKind.ShellVerb),
        new(ContextMenuCategory.Library, @"UserLibraryFolder\shellex\ContextMenuHandlers", ContextMenuEntryKind.ShellExtension),
        new(ContextMenuCategory.Library, @"UserLibraryFolder\shellex\-ContextMenuHandlers", ContextMenuEntryKind.ShellExtension, @"UserLibraryFolder\shellex\ContextMenuHandlers", true),
        new(ContextMenuCategory.Computer, @"CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shell", ContextMenuEntryKind.ShellVerb),
        new(ContextMenuCategory.Computer, @"CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shellex\ContextMenuHandlers", ContextMenuEntryKind.ShellExtension),
        new(ContextMenuCategory.Computer, @"CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shellex\-ContextMenuHandlers", ContextMenuEntryKind.ShellExtension, @"CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\shellex\ContextMenuHandlers", true),
        new(ContextMenuCategory.RecycleBin, @"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shell", ContextMenuEntryKind.ShellVerb),
        new(ContextMenuCategory.RecycleBin, @"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shellex\ContextMenuHandlers", ContextMenuEntryKind.ShellExtension),
        new(ContextMenuCategory.RecycleBin, @"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shellex\-ContextMenuHandlers", ContextMenuEntryKind.ShellExtension, @"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shellex\ContextMenuHandlers", true),
        new(ContextMenuCategory.RecycleBin, @"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\shellex\PropertySheetHandlers", ContextMenuEntryKind.ShellExtension)
    ];

    private static readonly HashSet<string> MonitoredStableRootPaths = MonitoredRoots
        .Select(static root => root.StableRelativePath)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly FileLogger _logger;
    private readonly ContextMenuStateStore _stateStore;
    private readonly RegistryBackupService _backupService;
    private readonly BackendProtectionSettingsStore _protectionSettingsStore;
    private readonly Windows11ContextMenuCatalog _windows11Catalog;
    private readonly OfficeSuiteCoexistenceDetector _officeCoexistenceDetector;
    private readonly SemaphoreSlim _persistentStateGate = new(1, 1);
    private readonly AsyncLocal<int> _persistentStateGateDepth = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextMenuRegistryCatalog"/> class.
    /// </summary>
    public ContextMenuRegistryCatalog(
        FileLogger logger,
        ContextMenuStateStore stateStore,
        RegistryBackupService backupService,
        BackendProtectionSettingsStore protectionSettingsStore)
    {
        _logger = logger;
        _stateStore = stateStore;
        _backupService = backupService;
        _protectionSettingsStore = protectionSettingsStore;
        _windows11Catalog = new Windows11ContextMenuCatalog();
        _officeCoexistenceDetector = new OfficeSuiteCoexistenceDetector(logger);
    }

    /// <summary>
    /// Gets snapshot Async.
    /// </summary>
    public async Task<IReadOnlyList<ContextMenuEntry>> GetSnapshotAsync(CancellationToken cancellationToken = default, BackendUserContext? userContext = null)
    {
        return await RunPersistentStateOperationAsync(
            async () => await BuildSnapshotAsync(
                await EnumerateActualEntriesAsync(cancellationToken, userContext),
                static state => MonitoredStableRootPaths.Contains(state.SourceRootPath)
                                || state.IsWindows11ContextMenu,
                persistDiscoveredStates: true,
                persistSnapshotUpdates: true,
                RegularBaselineMarkerId,
                allowBaselineInitialization: userContext is not null,
                cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Returns the number of persisted, non-deleted states that would normally be
    /// enumerated by <see cref="GetSnapshotAsync"/>. The monitor uses this as the
    /// expected baseline size to decide whether an interactive-session snapshot is
    /// complete before rebuilding its runtime baseline.
    /// </summary>
    public async Task<int> GetPersistedActiveStateCountAsync(CancellationToken cancellationToken = default)
    {
        var states = await _stateStore.LoadAsync(cancellationToken);
        return states.Values.Count(static state =>
            !state.IsDeleted
            && (MonitoredStableRootPaths.Contains(state.SourceRootPath) || state.IsWindows11ContextMenu));
    }

    public async Task<IReadOnlyList<ContextMenuEntry>> GetWpsOfficePendingApprovalsAsync(
        CancellationToken cancellationToken = default,
        BackendUserContext? userContext = null)
    {
        if (userContext is null)
        {
            return [];
        }

        return await RunPersistentStateOperationAsync(async () =>
        {
            var entries = _officeCoexistenceDetector.DetectSyntheticEntries(userContext);
            return await BuildSnapshotAsync(
                entries,
                static state => IsWpsOfficeSyntheticSource(state.SourceRootPath),
                persistDiscoveredStates: true,
                persistSnapshotUpdates: true,
                WpsOfficeBaselineMarkerId,
                allowBaselineInitialization: true,
                cancellationToken);
        }, cancellationToken);
    }

    private async Task<T> RunPersistentStateOperationAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (_persistentStateGateDepth.Value > 0)
        {
            _persistentStateGateDepth.Value++;
            try
            {
                return await operation();
            }
            finally
            {
                _persistentStateGateDepth.Value--;
            }
        }

        await _persistentStateGate.WaitAsync(cancellationToken);
        _persistentStateGateDepth.Value = 1;
        try
        {
            return await operation();
        }
        finally
        {
            _persistentStateGateDepth.Value = 0;
            _persistentStateGate.Release();
        }
    }

    private async Task RunPersistentStateOperationAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        await RunPersistentStateOperationAsync(async () =>
        {
            await operation();
            return true;
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<ContextMenuEntry>> GetReadOnlySnapshotAsync(CancellationToken cancellationToken = default)
    {
        return await BuildSnapshotAsync(
            await EnumerateActualEntriesAsync(cancellationToken),
            static state => MonitoredStableRootPaths.Contains(state.SourceRootPath) || state.IsWindows11ContextMenu,
            persistDiscoveredStates: false,
            persistSnapshotUpdates: false,
            RegularBaselineMarkerId,
            allowBaselineInitialization: false,
            cancellationToken);
    }

    /// <summary>
    /// Gets scene Snapshot Async.
    /// </summary>
    public async Task<IReadOnlyList<ContextMenuEntry>> GetSceneSnapshotAsync(
        ContextMenuSceneKind sceneKind,
        string? scopeValue,
        CancellationToken cancellationToken = default,
        BackendUserContext? userContext = null)
    {
        var roots = GetSceneRoots(sceneKind, scopeValue, userContext).ToArray();
        if (roots.Length == 0)
        {
            await LogCustomExtensionSceneDiagnosticsAsync(sceneKind, scopeValue, userContext, roots, [], cancellationToken);
            return [];
        }

        var includedRootPaths = roots
            .Select(static root => root.StableRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var snapshot = await BuildSnapshotAsync(
            EnumerateEntries(roots, userContext),
            state => includedRootPaths.Contains(state.SourceRootPath),
            persistDiscoveredStates: false,
            persistSnapshotUpdates: false,
            baselineMarkerId: null,
            allowBaselineInitialization: false,
            cancellationToken);

        await LogCustomExtensionSceneDiagnosticsAsync(sceneKind, scopeValue, userContext, roots, snapshot, cancellationToken);

        return snapshot
            .Select(static entry => entry with
            {
                IsPendingApproval = false,
                DetectedChangeKind = ContextMenuChangeKind.None,
                DetectedChangeDetails = null,
                HasConsistencyIssue = false,
                ConsistencyIssue = null
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<ContextMenuEntry>> FindRelatedFileTypeMenuItemsAsync(
        FileTypeBatchQuery query,
        CancellationToken cancellationToken = default,
        BackendUserContext? userContext = null)
    {
        var roots = CreateRelatedFileTypeRoots(userContext).ToArray();
        if (roots.Length == 0)
        {
            return [];
        }

        var includedRootPaths = roots
            .Select(static root => root.StableRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var snapshot = await BuildSnapshotAsync(
            EnumerateEntries(roots, userContext),
            state => includedRootPaths.Contains(state.SourceRootPath),
            persistDiscoveredStates: false,
            persistSnapshotUpdates: false,
            baselineMarkerId: null,
            allowBaselineInitialization: false,
            cancellationToken);

        var relatedActual = snapshot
            .Where(entry => IsRelatedFileTypeEntry(query, entry))
            .Select(static entry => entry with
            {
                IsPendingApproval = false,
                DetectedChangeKind = ContextMenuChangeKind.None,
                DetectedChangeDetails = null,
                HasConsistencyIssue = false,
                ConsistencyIssue = null
            });

        var states = await _stateStore.LoadAsync(cancellationToken);
        var relatedDeleted = states.Values
            .Where(static state => state.IsDeleted && !string.IsNullOrWhiteSpace(state.BackupFilePath))
            .Select(static state => state.ToDeletedEntry())
            .Where(entry => IsRelatedFileTypeEntry(query, entry))
            .Select(static entry => entry with
            {
                IsPendingApproval = false,
                DetectedChangeKind = ContextMenuChangeKind.None,
                DetectedChangeDetails = null,
                HasConsistencyIssue = false,
                ConsistencyIssue = null
            });

        var related = relatedActual
            .Concat(relatedDeleted)
            .GroupBy(static entry => string.IsNullOrWhiteSpace(entry.BackendRegistryPath) ? entry.Id : entry.BackendRegistryPath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderBy(static entry => entry.IsDeleted ? 1 : 0).First())
            .OrderBy(static entry => entry.RegistryPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        await _logger.LogAsync(
            $"RelatedFileTypeMenuItemsScan: EntryKind={query.EntryKind}, KeyName={query.KeyName}, CommandExecutablePath={query.CommandExecutablePath}, HandlerClsid={query.HandlerClsid}, FrontendSid={DiagnosticLogFormatter.FormatSid(userContext)}, ResultCount={related.Length}.",
            cancellationToken);

        return related;
    }

    public async Task<IReadOnlyList<ContextMenuEntry>> GetWindows11SnapshotAsync(
        CancellationToken cancellationToken = default,
        BackendUserContext? userContext = null)
    {
        return _windows11Catalog.IsSupported
            ? await _windows11Catalog.EnumerateEntriesAsync(cancellationToken, userContext)
            : [];
    }

    public async Task<PipeResponse> SetWindows11SystemCommandEnabledAsync(
        string commandKey,
        bool enable,
        Guid? operationId,
        CancellationToken cancellationToken = default)
    {
        return await _windows11Catalog.SetSystemCommandEnabledAsync(
            commandKey,
            enable,
            operationId,
            cancellationToken);
    }

    internal async Task<PipeResponse?> CreateRegistryWriteProtectionPreflightFailureAsync(
        string operationName,
        IEnumerable<string?> targetPaths,
        CancellationToken cancellationToken)
    {
        var settings = await _protectionSettingsStore.LoadAsync(cancellationToken);
        if (!settings.LockNewContextMenuItems)
        {
            return null;
        }

        var targets = targetPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await _logger.LogAsync(
            RuntimeLogLevel.Warning,
            $"RegistryWriteProtectionPreflightBlocked: Operation={operationName}, Targets={string.Join(";", targets)}.",
            cancellationToken);

        return new PipeResponse
        {
            Success = false,
            ErrorCode = PipeErrorCodes.RegistryWriteProtectionEnabled,
            RegistryProtectionEnabled = true,
            Message = "Registry write protection is enabled. Please disable the context-menu add/modify protection in Settings before editing, adding, disabling, or deleting context menu items."
        };
    }

    private async Task<IReadOnlyList<ContextMenuEntry>> BuildSnapshotAsync(
        IEnumerable<ContextMenuEntry> actualEntriesSource,
        Func<PersistedContextMenuState, bool> includePersistedState,
        bool persistDiscoveredStates,
        bool persistSnapshotUpdates,
        string? baselineMarkerId,
        bool allowBaselineInitialization,
        CancellationToken cancellationToken)
    {
        var states = await _stateStore.LoadAsync(cancellationToken);
        // A snapshot only has an established baseline when the state store already
        // contains states relevant to this snapshot kind. The regular menu and
        // WPS/Office synthetic sources share one state store but are fetched via
        // separate snapshot calls, so a global "any state exists" check would let
        // one kind (for example a WPS approval refresh running right after a state
        // reset) make the other kind's first snapshot look like mass external
        // additions instead of adopting them as the initial baseline.
        var hasLegacyBaseline = states.Values.Any(state => !state.IsDeleted && includePersistedState(state));
        var hasBaseline = HasPersistedBaseline(states, baselineMarkerId, includePersistedState);
        var isInitializingBaseline = persistDiscoveredStates
                                     && persistSnapshotUpdates
                                     && allowBaselineInitialization
                                     && !hasBaseline;
        // WPS/Office findings use a separate synthetic source and are fetched
        // after the regular menu snapshot. A regular snapshot may already have
        // populated the state store on first run, so it must not make existing
        // WPS associations look like newly detected changes.
        var hasWpsOfficeBaseline = states.ContainsKey(WpsOfficeBaselineMarkerId)
                                   || HasWpsOfficeSyntheticBaseline(states.Values);
        var actualEntries = new Dictionary<string, ContextMenuEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in actualEntriesSource.GroupBy(static item => item.Id, StringComparer.OrdinalIgnoreCase))
        {
            states.TryGetValue(group.Key, out var persistedState);
            actualEntries[group.Key] = await SelectAndNormalizeActualEntryAsync(
                group.ToArray(),
                persistedState?.DesiredEnabled,
                repairDuplicateContainers: persistSnapshotUpdates,
                cancellationToken);
        }

        var results = new List<ContextMenuEntry>();
        var dirty = false;
        var missingStateIdsToRemove = new List<string>();
        // A snapshot carrying an explicit frontend/interactive user context is
        // authoritative for missing-item cleanup. Context-free service snapshots
        // may run before user hives are available and therefore never prune.
        var preserveMissingStates = !allowBaselineInitialization;

        foreach (var entry in actualEntries.Values.OrderBy(static item => item.Category).ThenBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            states.TryGetValue(entry.Id, out var state);
            if (state is not null && state.IsDeleted)
            {
                // Deleted entries are recovery records only. They are excluded
                // from monitoring identity. If the registry key exists again,
                // discard the obsolete recovery record and classify the key as a
                // normal unknown item under the runtime/offline Added rules.
                if (!string.IsNullOrWhiteSpace(state.BackupFilePath))
                {
                    _backupService.DeleteBackupFile(state.BackupFilePath);
                }

                states.Remove(entry.Id);
                state = null;
                dirty = true;
            }

            if (state is null && IsWpsOfficeSyntheticId(entry.Id))
            {
                state = PersistedContextMenuState.FromEntry(entry);
                // A reset/first-run state store must adopt existing WPS findings
                // as the initial baseline. Otherwise every current WPS ShellNew,
                // association, or icon finding is incorrectly reintroduced into
                // Pending Approvals solely because its acknowledgement was reset.
                state.IsPendingApproval = OfficeSuiteCoexistenceDetector
                    .ShouldMarkNewFindingPendingApproval(hasWpsOfficeBaseline);
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;
                states[entry.Id] = state;
                dirty = true;
            }

            // 1.7.2 used this global CLSID list for classic Shell Extension
            // toggles. It is deliberately not mutated here: the value might have
            // been created by an administrator or another tool. Surface it as a
            // separate compatibility warning instead of claiming this physical
            // registration is effectively enabled.
            var hasLegacyGlobalShellExtensionBlock = HasLegacyGlobalShellExtensionBlock(entry);
            var issue = GetConsistencyIssue(entry, state, hasLegacyGlobalShellExtensionBlock);
            var changeKind = IsWpsOfficeSyntheticId(entry.Id)
                ? state?.IsPendingApproval == true
                    ? entry.DetectedChangeKind
                    : ContextMenuChangeKind.None
                : GetDetectedChangeKind(entry, state, hasBaseline);
            var changeDetails = IsWpsOfficeSyntheticId(entry.Id)
                ? changeKind == ContextMenuChangeKind.None
                    ? null
                    : entry.DetectedChangeDetails
                : GetDetectedChangeDetails(entry, state, changeKind);
            var merged = entry with
            {
                IsPendingApproval = state?.IsPendingApproval ?? false,
                HasBackup = !string.IsNullOrWhiteSpace(state?.BackupFilePath),
                DeletedAtUtc = state?.DeletedAtUtc,
                IsPresentInRegistry = true,
                HasConsistencyIssue = !string.IsNullOrWhiteSpace(issue),
                ConsistencyIssue = issue,
                HasLegacyGlobalShellExtensionBlock = hasLegacyGlobalShellExtensionBlock,
                DetectedChangeKind = changeKind,
                DetectedChangeDetails = changeDetails
            };

            results.Add(merged);

            if (state is null)
            {
                if (isInitializingBaseline)
                {
                    // The first persisted snapshot becomes the baseline that later
                    // runs compare against for change detection and approvals. Once
                    // a baseline exists, unknown entries must remain marked as Added
                    // until the user explicitly acknowledges them.
                    states[entry.Id] = PersistedContextMenuState.FromEntry(merged);
                    dirty = true;
                }

                continue;
            }

            if (persistSnapshotUpdates && state.ConsecutiveMissingSnapshots != 0)
            {
                state.ConsecutiveMissingSnapshots = 0;
                dirty = true;
            }

            if (persistSnapshotUpdates && changeKind == ContextMenuChangeKind.None)
            {
                dirty |= UpdateMetadata(state, merged);
            }
        }

        foreach (var state in states.Values
                     .Where(state => includePersistedState(state) && !actualEntries.ContainsKey(state.Id))
                     .OrderBy(static state => state.Category)
                     .ThenBy(static state => state.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (!state.IsDeleted && preserveMissingStates)
            {
                // Service startup can happen before all per-user classes and packaged
                // COM registrations are fully visible. Keeping the baseline alive
                // until the first fully interactive snapshot has completed prevents the entire
                // catalog from being
                // re-quarantined as "new" once those registrations appear.
                continue;
            }

            // External removals are intentionally silent in the UI, but they still
            // need to be removed from the persisted baseline. Otherwise a later
            // reinstall looks like an old known item instead of a genuinely new one.
            if (ContextMenuChangeClassifier.ShouldRemoveMissingState(state))
            {
                if (!persistSnapshotUpdates)
                {
                    continue;
                }

                state.ConsecutiveMissingSnapshots++;
                dirty = true;
                if (state.ConsecutiveMissingSnapshots < 2)
                {
                    // Require a settled item to be absent in more than one stable
                    // snapshot before removing its baseline. This avoids startup
                    // races where shell registrations arrive one polling cycle later.
                    continue;
                }

                missingStateIdsToRemove.Add(state.Id);
                continue;
            }

            var issue = state.IsDeleted
                ? GetDeletedConsistencyIssue(state)
                : "The menu item is missing from the registry.";
            var changeKind = !state.IsDeleted && hasBaseline
                ? ContextMenuChangeKind.Removed
                : ContextMenuChangeKind.None;
            var changeDetails = changeKind == ContextMenuChangeKind.Removed
                ? "This item existed the last time the context menu catalog was scanned, but it is now missing from the registry."
                : null;

            results.Add(CreateVirtualEntry(state, issue, changeKind, changeDetails));
        }

        foreach (var stateId in missingStateIdsToRemove)
        {
            states.Remove(stateId);
        }

        if (isInitializingBaseline && baselineMarkerId is not null)
        {
            states[baselineMarkerId] = CreateBaselineMarker(baselineMarkerId);
            dirty = true;
        }
        else if (hasLegacyBaseline
                 && baselineMarkerId is not null
                 && !states.ContainsKey(baselineMarkerId)
                 && persistSnapshotUpdates)
        {
            // One-time migration for state databases created before explicit
            // per-source baseline markers existed.
            states[baselineMarkerId] = CreateBaselineMarker(baselineMarkerId);
            dirty = true;
        }

        PruneTransientStates(states);

        if (dirty)
        {
            await _stateStore.SaveAsync(states, cancellationToken);
        }

        return results;
    }

    private static PersistedContextMenuState CreateBaselineMarker(string markerId)
        => new()
        {
            Id = markerId,
            DisplayName = markerId,
            SourceRootPath = BaselineMarkerSourceRootPath,
            IsDeleted = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

    internal static bool HasPersistedBaseline(
        IReadOnlyDictionary<string, PersistedContextMenuState> states,
        string? baselineMarkerId,
        Func<PersistedContextMenuState, bool> includePersistedState)
        => baselineMarkerId is not null && states.ContainsKey(baselineMarkerId)
           || states.Values.Any(state => !state.IsDeleted && includePersistedState(state));

    /// <summary>
    /// Applies desired State Async.
    /// </summary>
    public async Task<PipeResponse> ApplyDesiredStateAsync(
        string itemId,
        bool enable,
        CancellationToken cancellationToken,
        BackendUserContext? userContext = null,
        ContextMenuEntry? fallbackItem = null)
        => await RunPersistentStateOperationAsync(
            () => ApplyDesiredStateCoreAsync(itemId, enable, cancellationToken, userContext, fallbackItem),
            cancellationToken);

    private async Task<PipeResponse> ApplyDesiredStateCoreAsync(
        string itemId,
        bool enable,
        CancellationToken cancellationToken,
        BackendUserContext? userContext,
        ContextMenuEntry? fallbackItem)
    {
        if (string.Equals(itemId, RecycleBinPinToHomeId, StringComparison.OrdinalIgnoreCase))
        {
            return await ApplyRecycleBinPinToHomeStateAsync(enable, cancellationToken, userContext);
        }

        var snapshot = await GetSnapshotAsync(cancellationToken, userContext);
        var item = snapshot.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = TryUseSceneFallbackItem(itemId, fallbackItem);
        }

        if (item is null)
        {
            return CreateFailure($"Menu item '{itemId}' was not found.");
        }

        if (item.IsDeleted)
        {
            return CreateFailure($"Menu item '{item.DisplayName}' is deleted. Undo the deletion before changing its state.");
        }

        if (!item.CanToggle)
        {
            return CreateFailure($"Menu item '{item.DisplayName}' uses a Shell Extension registration type without a verified enable/disable operation.", item);
        }

        var preflight = await CreateRegistryWriteProtectionPreflightFailureAsync(
            "SetEnabled",
            [item.BackendRegistryPath, item.RegistryPath, item.SourceRootPath],
            cancellationToken);
        if (preflight is not null)
        {
            return preflight with { Item = item };
        }

        try
        {
            if (item.IsWindows11ContextMenu)
            {
                // Win11 packaged verbs do not use the classic shell verb/handler
                // write paths, so they are toggled through the blocked-extension list.
                if (!_windows11Catalog.SetEnabled(item.HandlerClsid ?? item.KeyName, item.DisplayName, userContext, enable))
                {
                    return CreateFailure($"Unable to update the Win11 context menu item '{item.DisplayName}'.");
                }
            }
            else
                switch (item.EntryKind)
                {
                    case ContextMenuEntryKind.ShellVerb:
                        SetShellVerbEnabled(item.BackendRegistryPath, item.RegistryPath, enable);
                        break;
                    case ContextMenuEntryKind.ShellExtension:
                        await SetShellExtensionEnabledAsync(item, enable, cancellationToken);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported entry kind: {item.EntryKind}");
                }

            IReadOnlyList<ContextMenuEntry> physicalCandidates = [];
            if (item.EntryKind == ContextMenuEntryKind.ShellVerb && !item.IsWindows11ContextMenu)
            {
                // A File Types or scene item can live outside MonitoredRoots. Re-open
                // the exact stable source before asking the normal catalog to project
                // it, because the normal snapshot intentionally does not enumerate
                // every ProgID root in Software\Classes.
                physicalCandidates = await FindEntriesByIdAsync(itemId, cancellationToken, userContext);
            }

            var refreshedLogical = (await GetSnapshotAsync(cancellationToken, userContext))
                .FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase));
            var refreshed = refreshedLogical;
            ShellVerbMutationReconciliation? shellVerbReconciliation = null;
            if (item.EntryKind == ContextMenuEntryKind.ShellVerb && !item.IsWindows11ContextMenu)
            {
                shellVerbReconciliation = ReconcileShellVerbMutation(
                    item,
                    physicalCandidates,
                    refreshedLogical,
                    enable);
                refreshed = shellVerbReconciliation.Entry;
            }

            if (shellVerbReconciliation is { IsVerified: false }
                || refreshed is null
                || refreshed.IsEnabled != enable)
            {
                await _logger.LogAsync(
                    RuntimeLogLevel.Warning,
                    $"SetEnabledVerificationFailed: ItemId={item.Id}, EntryKind={item.EntryKind}, Category={item.Category}, HandlerClsid={item.HandlerClsid ?? "<none>"}, RequestedEnabled={enable}, RegistryPath={item.RegistryPath}, BackendRegistryPath={item.BackendRegistryPath}, SourceRootPath={item.SourceRootPath}, KeyName={item.KeyName}, PhysicalCandidateCount={shellVerbReconciliation?.MatchingPhysicalCandidateCount}, PhysicalTargetExists={shellVerbReconciliation?.TargetPathExists}, PhysicalMismatchedPaths={shellVerbReconciliation?.MismatchedPhysicalPathsText ?? "<none>"}, RefreshedItemId={refreshedLogical?.Id ?? "<none>"}, RefreshedBackendRegistryPath={refreshedLogical?.BackendRegistryPath ?? "<none>"}, RefreshedIsEnabled={refreshedLogical?.IsEnabled}, RefreshedHasConsistencyIssue={refreshedLogical?.HasConsistencyIssue}, LogicalCandidateCount={shellVerbReconciliation?.MatchingLogicalCandidateCount}, DesiredEnabled={shellVerbReconciliation?.DesiredEnabled}, ObservedEnabled={shellVerbReconciliation?.ObservedEnabled}, ReconciliationFailure={shellVerbReconciliation?.FailureReason ?? "<none>"}, ErrorCode={PipeErrorCodes.RegistryMutationVerificationFailed}.",
                    cancellationToken);
                throw new ProtectedRegistryMutationException(
                    PipeErrorCodes.RegistryMutationVerificationFailed,
                    $"The registry change for '{item.DisplayName}' could not be verified after refresh.");
            }

            if (shellVerbReconciliation is { UsedPhysicalSourceFallback: true })
            {
                await _logger.LogAsync(
                    $"SetEnabledLogicalReconciliationUsedPhysicalSource: ItemId={item.Id}, EntryKind={item.EntryKind}, Category={item.Category}, RequestedEnabled={enable}, RegistryPath={item.RegistryPath}, BackendRegistryPath={item.BackendRegistryPath}, SourceRootPath={item.SourceRootPath}, KeyName={item.KeyName}, PhysicalCandidateCount={shellVerbReconciliation.MatchingPhysicalCandidateCount}, LogicalCandidateCount={shellVerbReconciliation.MatchingLogicalCandidateCount}, DesiredEnabled={shellVerbReconciliation.DesiredEnabled}, ObservedEnabled={shellVerbReconciliation.ObservedEnabled}.",
                    cancellationToken);
            }

            var states = await _stateStore.LoadAsync(cancellationToken);
            var linkedEntries = GetStateLinkedEntries(snapshot, item);
            foreach (var linkedEntry in linkedEntries)
            {
                // One user gesture may affect several projected entries, so we keep
                // their persisted desired/observed state in sync here.
                var state = GetOrCreateState(states, linkedEntry);
                state.DesiredEnabled = enable;
                state.ObservedEnabled = enable;
                state.IsDeleted = false;
                state.IsPendingApproval = false;
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;
                state.DeletedAtUtc = null;
                state.BackupFilePath = null;
            }

            PruneTransientStates(states);
            await _stateStore.SaveAsync(states, cancellationToken);
            ShellChangeNotifier.NotifyAssociationsChanged();

            // Re-fetch the item after the approval state was persisted. The
            // earlier `refreshed` snapshot was captured before IsPendingApproval
            // was cleared, so returning it would keep the approval card visible
            // and force the user to click Allow/Deny a second time.
            var finalItem = (await GetSnapshotAsync(cancellationToken, userContext))
                .FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase));

            await _logger.LogAsync($"{(enable ? "Enabled" : "Disabled")} {item.DisplayName} ({item.RegistryPath}).", cancellationToken);

            return new PipeResponse
            {
                Success = true,
                Message = $"{(enable ? "Enabled" : "Disabled")} {item.DisplayName}.",
                Item = finalItem ?? refreshed
            };
        }
        catch (ProtectedRegistryMutationException ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Warning, $"Protected registry mutation failed. ItemId={item.Id}, EntryKind={item.EntryKind}, HandlerClsid={item.HandlerClsid ?? "<none>"}, RequestedEnabled={enable}, RegistryPath={item.RegistryPath}, BackendRegistryPath={item.BackendRegistryPath}, ErrorCode={ex.ErrorCode}, Exception={ex}", cancellationToken);
            return CreateFailure(ex.Message, item, ex.ErrorCode);
        }
        catch (UnauthorizedAccessException ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Warning, $"Registry access denied. ItemId={item.Id}, EntryKind={item.EntryKind}, HandlerClsid={item.HandlerClsid ?? "<none>"}, RequestedEnabled={enable}, RegistryPath={item.RegistryPath}, BackendRegistryPath={item.BackendRegistryPath}, Exception={ex}", cancellationToken);
            return CreateFailure("Windows denied access to the registry entry. No changes were applied.", item, PipeErrorCodes.ProtectedRegistryMutationFailed);
        }
        catch (Exception ex)
        {
            await _logger.LogAsync($"Failed to update {item.DisplayName}: {ex.Message}", cancellationToken);
            return CreateFailure(ex.Message, item);
        }
    }

    /// <summary>
    /// Applies decision Async.
    /// </summary>
    public async Task<PipeResponse> ApplyDecisionAsync(
        string itemId,
        ContextMenuDecision decision,
        CancellationToken cancellationToken,
        BackendUserContext? userContext = null)
        => await RunPersistentStateOperationAsync(
            () => ApplyDecisionCoreAsync(itemId, decision, cancellationToken, userContext),
            cancellationToken);

    private async Task<PipeResponse> ApplyDecisionCoreAsync(
        string itemId,
        ContextMenuDecision decision,
        CancellationToken cancellationToken,
        BackendUserContext? userContext)
    {
        if (IsWpsOfficeSyntheticId(itemId))
        {
            var wpsSnapshot = await GetWpsOfficePendingApprovalsAsync(cancellationToken, userContext);
            var wpsItem = wpsSnapshot.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase));
            return await AcknowledgeWpsOfficeSyntheticStateAsync(itemId, wpsItem, cancellationToken);
        }

        var snapshot = await GetSnapshotAsync(cancellationToken, userContext);
        var item = snapshot.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase));

        return decision switch
        {
            ContextMenuDecision.Allow => item is null
                ? CreateFailure($"Menu item '{itemId}' was not found.")
                : await ApplyDesiredStateAsync(itemId, enable: true, cancellationToken, userContext),
            ContextMenuDecision.Deny => item is null
                ? await RemovePendingApprovalStateAsync(itemId, cancellationToken)
                : await ApplyDesiredStateAsync(itemId, enable: false, cancellationToken, userContext),
            ContextMenuDecision.Remove => await RemovePendingApprovalItemAsync(item, itemId, cancellationToken),
            _ => CreateFailure("Unknown approval decision.")
        };
    }
    /// <summary>
    /// Executes acknowledge Item State Async.
    /// </summary>
    public async Task<PipeResponse> AcknowledgeItemStateAsync(string itemId, CancellationToken cancellationToken)
        => await RunPersistentStateOperationAsync(
            () => AcknowledgeItemStateCoreAsync(itemId, cancellationToken),
            cancellationToken);

    private async Task<PipeResponse> AcknowledgeItemStateCoreAsync(string itemId, CancellationToken cancellationToken)
    {
        var states = await _stateStore.LoadAsync(cancellationToken);
        var actualEntry = (await EnumerateActualEntriesAsync(cancellationToken))
            .FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase));

        if (actualEntry is not null)
        {
            states.TryGetValue(itemId, out var previousState);
            if (previousState is not null && !string.IsNullOrWhiteSpace(previousState.BackupFilePath))
            {
                _backupService.DeleteBackupFile(previousState.BackupFilePath);
            }

            var state = GetOrCreateState(states, actualEntry);
            state.IsDeleted = false;
            state.IsPendingApproval = false;
            state.SuppressNextDetection = false;
            state.BackupFilePath = null;
            state.DeletedAtUtc = null;
            state.DesiredEnabled = actualEntry.IsEnabled;
            state.ObservedEnabled = actualEntry.IsEnabled;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            PruneTransientStates(states);
            await _stateStore.SaveAsync(states, cancellationToken);

            var refreshed = (await GetSnapshotAsync(cancellationToken))
                .FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase))
                ?? actualEntry;

            return new PipeResponse
            {
                Success = true,
                Message = $"Synchronized {refreshed.DisplayName} with the current registry state.",
                Item = refreshed
            };
        }

        if (!states.TryGetValue(itemId, out var persistedState))
        {
            return new PipeResponse
            {
                Success = true,
                Message = $"Item '{itemId}' is already synchronized."
            };
        }

        if (!persistedState.IsDeleted)
        {
            states.Remove(itemId);
            PruneTransientStates(states);
            await _stateStore.SaveAsync(states, cancellationToken);

            return new PipeResponse
            {
                Success = true,
                Message = $"Removed stale state for {persistedState.DisplayName}."
            };
        }

        persistedState.IsPendingApproval = false;
        persistedState.SuppressNextDetection = false;
        persistedState.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _stateStore.SaveAsync(states, cancellationToken);

        return new PipeResponse
        {
            Success = true,
            Message = $"Acknowledged the current deleted state for {persistedState.DisplayName}.",
            Item = persistedState.ToDeletedEntry()
        };
    }

    /// <summary>
    /// Applies shell Attribute Async.
    /// </summary>
    public async Task<PipeResponse> ApplyShellAttributeAsync(
        string itemId,
        ContextMenuShellAttribute attribute,
        bool enable,
        CancellationToken cancellationToken)
        => await RunPersistentStateOperationAsync(
            () => ApplyShellAttributeCoreAsync(itemId, attribute, enable, cancellationToken),
            cancellationToken);

    private async Task<PipeResponse> ApplyShellAttributeCoreAsync(
        string itemId,
        ContextMenuShellAttribute attribute,
        bool enable,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var item = snapshot.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return CreateFailure($"Menu item '{itemId}' was not found.");
        }

        if (item.EntryKind != ContextMenuEntryKind.ShellVerb)
        {
            return CreateFailure("Only shell verb items support extra shell attributes.", item);
        }

        if (item.IsDeleted)
        {
            return CreateFailure($"Menu item '{item.DisplayName}' is deleted. Undo the deletion before editing its attributes.", item);
        }

        try
        {
            SetShellVerbAttribute(item.BackendRegistryPath, attribute, enable);

            var states = await _stateStore.LoadAsync(cancellationToken);
            var state = GetOrCreateState(states, item);
            state.OnlyWithShift = attribute == ContextMenuShellAttribute.OnlyWithShift ? enable : state.OnlyWithShift;
            state.OnlyInExplorer = attribute == ContextMenuShellAttribute.OnlyInExplorer ? enable : state.OnlyInExplorer;
            state.NoWorkingDirectory = attribute == ContextMenuShellAttribute.NoWorkingDirectory ? enable : state.NoWorkingDirectory;
            state.NeverDefault = attribute == ContextMenuShellAttribute.NeverDefault ? enable : state.NeverDefault;
            state.ShowAsDisabledIfHidden = attribute == ContextMenuShellAttribute.ShowAsDisabledIfHidden ? enable : state.ShowAsDisabledIfHidden;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _stateStore.SaveAsync(states, cancellationToken);
            ShellChangeNotifier.NotifyAssociationsChanged();

            await _logger.LogAsync($"Set attribute {attribute}={(enable ? "on" : "off")} for {item.DisplayName} ({item.RegistryPath}).", cancellationToken);

            var refreshed = (await GetSnapshotAsync(cancellationToken))
                .FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase))
                ?? item;

            return new PipeResponse
            {
                Success = true,
                Message = $"Updated {attribute} for {item.DisplayName}.",
                Item = refreshed
            };
        }
        catch (Exception ex)
        {
            await _logger.LogAsync($"Failed to set {attribute} for {item.DisplayName}: {ex.Message}", cancellationToken);
            return CreateFailure(ex.Message, item);
        }
    }

    /// <summary>
    /// Applies display Text Async.
    /// </summary>
    public async Task<PipeResponse> ApplyDisplayTextAsync(string itemId, string textValue, CancellationToken cancellationToken)
        => await RunPersistentStateOperationAsync(
            () => ApplyDisplayTextCoreAsync(itemId, textValue, cancellationToken),
            cancellationToken);

    private async Task<PipeResponse> ApplyDisplayTextCoreAsync(string itemId, string textValue, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var item = snapshot.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return CreateFailure($"Menu item '{itemId}' was not found.");
        }

        if (item.EntryKind != ContextMenuEntryKind.ShellVerb)
        {
            return CreateFailure("Only shell verb items support text changes.", item);
        }

        if (item.IsDeleted)
        {
            return CreateFailure($"Menu item '{item.DisplayName}' is deleted. Undo the deletion before changing its text.", item);
        }

        if (!CanEditDisplayText(item))
        {
            return CreateFailure("This menu item does not support text changes.", item);
        }

        if (string.IsNullOrWhiteSpace(textValue))
        {
            return CreateFailure("Menu text cannot be empty.", item);
        }

        var parsedText = ShellMetadataResolver.ResolveResourceString(textValue);
        if (string.IsNullOrWhiteSpace(parsedText))
        {
            return CreateFailure("The provided menu text could not be resolved.", item);
        }

        if (parsedText.Length >= 80)
        {
            return CreateFailure("The resolved menu text is too long.", item);
        }

        try
        {
            using var menuKey = OpenRegistryKey(item.BackendRegistryPath, writable: true)
                ?? throw new InvalidOperationException($"Unable to open {item.RegistryPath} for writing.");
            menuKey.SetValue("MUIVerb", textValue, RegistryValueKind.String);

            var states = await _stateStore.LoadAsync(cancellationToken);
            var state = GetOrCreateState(states, item);
            state.DisplayName = NormalizeDisplayName(parsedText);
            state.EditableText = parsedText;
            state.ObservedEnabled = item.IsEnabled;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _stateStore.SaveAsync(states, cancellationToken);
            ShellChangeNotifier.NotifyAssociationsChanged();

            var refreshed = (await GetSnapshotAsync(cancellationToken))
                .FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase))
                ?? item;

            return new PipeResponse
            {
                Success = true,
                Message = $"Updated display text for {item.DisplayName}.",
                Item = refreshed
            };
        }
        catch (Exception ex)
        {
            await _logger.LogAsync($"Failed to update display text for {item.DisplayName}: {ex.Message}", cancellationToken);
            return CreateFailure(ex.Message, item);
        }
    }

    public async Task<PipeResponse> ApplyCommandTextAsync(string itemId, string commandText, CancellationToken cancellationToken)
        => await RunPersistentStateOperationAsync(
            () => ApplyCommandTextCoreAsync(itemId, commandText, cancellationToken),
            cancellationToken);

    private async Task<PipeResponse> ApplyCommandTextCoreAsync(string itemId, string commandText, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var item = snapshot.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return CreateFailure($"Menu item '{itemId}' was not found.");
        }

        if (item.EntryKind != ContextMenuEntryKind.ShellVerb)
        {
            return CreateFailure("Only shell verb items support command text changes.", item);
        }

        if (item.IsWindows11ContextMenu)
        {
            return CreateFailure("Windows 11 context menu items do not support command text changes here.", item);
        }

        if (!item.IsPresentInRegistry || item.IsDeleted)
        {
            return CreateFailure($"Menu item '{item.DisplayName}' must exist in the registry before changing its command.", item);
        }

        if (!item.CanEditCommandText)
        {
            return CreateFailure("This menu item does not support command text changes.", item);
        }

        if (string.IsNullOrWhiteSpace(commandText))
        {
            return CreateFailure("Command cannot be empty.", item);
        }

        using (var itemKey = OpenRegistryKey(item.BackendRegistryPath, writable: false))
        {
            if (itemKey is null)
            {
                return CreateFailure($"Unable to open {item.RegistryPath}.", item);
            }

            using var commandKey = itemKey.OpenSubKey("command", writable: false);
            if (!CanEditCommandText(itemKey, commandKey))
            {
                return CreateFailure("This menu item does not support command text changes.", item);
            }
        }

        var preflight = await CreateRegistryWriteProtectionPreflightFailureAsync(
            "ApplyCommandText",
            [item.BackendRegistryPath, item.RegistryPath, item.SourceRootPath],
            cancellationToken);
        if (preflight is not null)
        {
            return preflight with { Item = item };
        }

        try
        {
            var commandPath = $@"{item.BackendRegistryPath}\command";
            using var commandKey = CreateRegistrySubKey(commandPath, writable: true)
                ?? throw new InvalidOperationException($"Unable to open {item.RegistryPath}\\command for writing.");
            var oldValue = commandKey.GetValue(null);
            commandKey.SetValue(string.Empty, commandText, RegistryValueKind.String);
            await _logger.LogAsync(
                DiagnosticLogFormatter.BuildRegistryOperationLog(
                    "ApplyCommandText",
                    commandPath,
                    "(Default)",
                    RegistryValueKind.String,
                    commandText,
                    writable: true,
                    result: $"Success, OldValue={DiagnosticLogFormatter.FormatRegistryValueData(oldValue)}"),
                cancellationToken);

            ShellChangeNotifier.NotifyAssociationsChanged();

            var refreshed = (await GetSnapshotAsync(cancellationToken))
                .FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase))
                ?? item with
                {
                    CommandText = commandText,
                    CanEditCommandText = true,
                    Notes = BuildNotes(item.EntryKind, commandText, item.HandlerClsid)
                };

            var states = await _stateStore.LoadAsync(cancellationToken);
            var state = GetOrCreateState(states, refreshed);
            UpdateMetadata(state, refreshed);
            state.ObservedEnabled = refreshed.IsEnabled;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _stateStore.SaveAsync(states, cancellationToken);

            return new PipeResponse
            {
                Success = true,
                Message = $"Updated command text for {item.DisplayName}.",
                Item = refreshed with
                {
                    DetectedChangeKind = ContextMenuChangeKind.None,
                    DetectedChangeDetails = null,
                    HasConsistencyIssue = false,
                    ConsistencyIssue = null
                }
            };
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Error, $"Failed to update command text for {item.DisplayName}: {ex}", cancellationToken);
            return CreateFailure(ex.Message, item);
        }
    }

    /// <summary>
    /// Sets enhance Menu Item Enabled Async.
    /// </summary>
    public async Task<PipeResponse> SetEnhanceMenuItemEnabledAsync(
        string groupRegistryPath,
        string definitionXml,
        bool enable,
        string? cultureName,
        BackendUserContext? userContext,
        CancellationToken cancellationToken)
        => await RunPersistentStateOperationAsync(
            () => SetEnhanceMenuItemEnabledCoreAsync(
                groupRegistryPath,
                definitionXml,
                enable,
                cultureName,
                userContext,
                cancellationToken),
            cancellationToken);

    private async Task<PipeResponse> SetEnhanceMenuItemEnabledCoreAsync(
        string groupRegistryPath,
        string definitionXml,
        bool enable,
        string? cultureName,
        BackendUserContext? userContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(groupRegistryPath))
        {
            return CreateFailure("The enhance-menu group registry path is required.");
        }

        if (string.IsNullOrWhiteSpace(definitionXml))
        {
            return CreateFailure("The enhance-menu item definition is required.");
        }

        if (userContext is null)
        {
            return CreateFailure("This operation requires an interactive user context.");
        }

        try
        {
            var itemElement = XElement.Parse(definitionXml);
            var relativeGroupPath = NormalizeClassesRootRelativePath(groupRegistryPath)
                ?? throw new InvalidOperationException("The enhance-menu group path must point into HKCR.");
            var requestedCultureName = cultureName?.Trim();
            var effectiveCultureName = NormalizeEnhanceCultureName(cultureName);
            EnhanceAttributeWriteResult? enhanceWriteResult = null;

            if (itemElement.Attribute("KeyName") is not null)
            {
                enhanceWriteResult = SetEnhanceShellItemEnabled(relativeGroupPath, itemElement, enable, effectiveCultureName, userContext, _logger);
            }
            else if (itemElement.Element("Guid") is not null)
            {
                SetEnhanceShellExItemEnabled(relativeGroupPath, itemElement, enable, userContext);
            }
            else
            {
                throw new InvalidOperationException("The enhance-menu item definition could not be recognized.");
            }

            try
            {
                await SyncEnhanceMenuStateAsync(relativeGroupPath, itemElement, enable, userContext, cancellationToken);
            }
            catch (Exception ex)
            {
                await _logger.LogAsync(
                    $"Enhance menu registry update succeeded but state sync failed under {groupRegistryPath}. "
                    + $"KeyName={itemElement.Attribute("KeyName")?.Value?.Trim()}, Guid={itemElement.Element("Guid")?.Value?.Trim()}, Enable={enable}: {ex.Message}",
                    cancellationToken);
            }

            ShellChangeNotifier.NotifyAssociationsChanged();

            if (enable && itemElement.Attribute("KeyName") is not null)
            {
                await _logger.LogAsync(
                    "EnhanceShellItemWrite: "
                    + $"RequestedCulture={requestedCultureName ?? string.Empty}, "
                    + $"NormalizedCulture={effectiveCultureName}, "
                    + $"KeyName={itemElement.Attribute("KeyName")?.Value?.Trim()}, "
                    + $"SelectedMUIVerb={enhanceWriteResult?.MuiVerb ?? string.Empty}, "
                    + $"CultureOverrideApplied={enhanceWriteResult?.CultureOverrideApplied ?? false}.",
                    cancellationToken);
            }

            await _logger.LogAsync(
                $"{(enable ? "Enabled" : "Disabled")} enhance menu item under {groupRegistryPath}. "
                + $"KeyName={itemElement.Attribute("KeyName")?.Value?.Trim()}, Guid={itemElement.Element("Guid")?.Value?.Trim()}, Culture={effectiveCultureName}.",
                cancellationToken);

            return new PipeResponse
            {
                Success = true,
                Message = enable
                    ? "Enhance menu item enabled."
                    : "Enhance menu item disabled."
            };
        }
        catch (Exception ex)
        {
            await _logger.LogAsync($"Failed to update enhance menu item: {ex.Message}", cancellationToken);
            return CreateFailure(ex.Message);
        }
    }

    /// <summary>
    /// Sets detailed edit rule value Async.
    /// </summary>
    public async Task<PipeResponse> SetDetailedEditRuleValueAsync(
        string? storageKind,
        string? path,
        string? section,
        string? keyName,
        string? valueKind,
        string? value,
        string? userSid,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storageKind))
        {
            return CreateFailure("The detailed edit rule storage kind is required.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return CreateFailure("The detailed edit rule path is required.");
        }

        if (string.IsNullOrWhiteSpace(keyName))
        {
            return CreateFailure("The detailed edit rule key name is required.");
        }

        try
        {
            if (string.Equals(storageKind, "Registry", StringComparison.OrdinalIgnoreCase))
            {
                WriteDetailedEditRegistryValue(path, keyName, valueKind, value, userSid);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported backend detailed edit storage kind: {storageKind}");
            }

            await _logger.LogAsync(
                $"Updated detailed edit rule value. Path={path}, KeyName={keyName}, ValueKind={valueKind}, Value={(value is null ? "<delete>" : value)}.",
                cancellationToken);

            return new PipeResponse
            {
                Success = true,
                Message = "Detailed edit rule value updated."
            };
        }
        catch (Exception ex)
        {
            await _logger.LogAsync($"Failed to update detailed edit rule value {path}\\{keyName}: {ex.Message}", cancellationToken);
            return CreateFailure(ex.Message);
        }
    }

    private async Task SyncEnhanceMenuStateAsync(
        string relativeGroupPath,
        XElement itemElement,
        bool enable,
        BackendUserContext userContext,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken, userContext);
        var states = await _stateStore.LoadAsync(cancellationToken);
        var matchingEntry = FindEnhanceMenuEntry(snapshot, relativeGroupPath, itemElement);

        if (enable)
        {
            if (matchingEntry is not null)
            {
                var state = GetOrCreateState(states, matchingEntry);
                state.IsPendingApproval = false;
                state.IsDeleted = false;
                state.SuppressNextDetection = true;
                state.DesiredEnabled = matchingEntry.IsEnabled;
                state.ObservedEnabled = matchingEntry.IsEnabled;
                state.BackupFilePath = null;
                state.DeletedAtUtc = null;
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await _stateStore.SaveAsync(states, cancellationToken);
            }

            return;
        }

        var removed = false;
        foreach (var state in states.Values
                     .Where(state => IsMatchingEnhanceMenuState(state, relativeGroupPath, itemElement))
                     .ToList())
        {
            state.IsPendingApproval = false;
            state.SuppressNextDetection = false;
            state.IsDeleted = false;
            state.BackupFilePath = null;
            state.DeletedAtUtc = null;
            state.DesiredEnabled = null;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;

            if (matchingEntry is null)
            {
                states.Remove(state.Id);
                removed = true;
            }
        }

        if (!removed && matchingEntry is not null)
        {
            var state = GetOrCreateState(states, matchingEntry);
            state.IsPendingApproval = false;
            state.SuppressNextDetection = false;
            state.DesiredEnabled = matchingEntry.IsEnabled;
            state.ObservedEnabled = matchingEntry.IsEnabled;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        await _stateStore.SaveAsync(states, cancellationToken);
    }

    private static ContextMenuEntry? FindEnhanceMenuEntry(
        IEnumerable<ContextMenuEntry> snapshot,
        string relativeGroupPath,
        XElement itemElement)
    {
        if (itemElement.Attribute("KeyName") is not null)
        {
            var keyName = itemElement.Attribute("KeyName")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(keyName))
            {
                return null;
            }

            return snapshot.FirstOrDefault(entry =>
                entry.EntryKind == ContextMenuEntryKind.ShellVerb
                && string.Equals(entry.SourceRootPath, $@"{relativeGroupPath}\shell", StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.KeyName, keyName, StringComparison.OrdinalIgnoreCase));
        }

        var guidText = itemElement.Element("Guid")?.Value?.Trim();
        if (!Guid.TryParse(guidText, out var guid))
        {
            return null;
        }

        return snapshot.FirstOrDefault(entry =>
            entry.EntryKind == ContextMenuEntryKind.ShellExtension
            && string.Equals(entry.SourceRootPath, $@"{relativeGroupPath}\shellex\ContextMenuHandlers", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(entry.HandlerClsid, out var handlerGuid)
            && handlerGuid == guid);
    }

    private static bool IsMatchingEnhanceMenuState(
        PersistedContextMenuState state,
        string relativeGroupPath,
        XElement itemElement)
    {
        if (itemElement.Attribute("KeyName") is not null)
        {
            var keyName = itemElement.Attribute("KeyName")?.Value?.Trim();
            return state.EntryKind == ContextMenuEntryKind.ShellVerb
                   && string.Equals(state.SourceRootPath, $@"{relativeGroupPath}\shell", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(state.KeyName, keyName, StringComparison.OrdinalIgnoreCase);
        }

        var guidText = itemElement.Element("Guid")?.Value?.Trim();
        return state.EntryKind == ContextMenuEntryKind.ShellExtension
               && string.Equals(state.SourceRootPath, $@"{relativeGroupPath}\shellex\ContextMenuHandlers", StringComparison.OrdinalIgnoreCase)
               && Guid.TryParse(guidText, out var expectedGuid)
               && Guid.TryParse(state.HandlerClsid, out var stateGuid)
               && expectedGuid == stateGuid;
    }

    /// <summary>
    /// Gets registry Protection Setting Async.
    /// </summary>
    public async Task<PipeResponse> GetRegistryProtectionSettingAsync(CancellationToken cancellationToken)
    {
        var settings = await _protectionSettingsStore.LoadAsync(cancellationToken);
        return new PipeResponse
        {
            Success = true,
            Message = "Registry protection setting loaded.",
            RegistryProtectionEnabled = settings.LockNewContextMenuItems
        };
    }

    /// <summary>
    /// Sets registry Protection Setting Async.
    /// </summary>
    public async Task<PipeResponse> SetRegistryProtectionSettingAsync(bool enable, BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        var errors = ApplyRegistryWriteProtection(enable, userContext);
        if (errors.Count > 0)
        {
            var detail = string.Join(Environment.NewLine, errors);
            await _logger.LogAsync($"Registry write protection update skipped some protected roots:{Environment.NewLine}{detail}", cancellationToken);
        }

        var settings = await _protectionSettingsStore.LoadAsync(cancellationToken);
        settings.LockNewContextMenuItems = enable;
        await _protectionSettingsStore.SaveAsync(settings, cancellationToken);
        await _logger.LogAsync($"Registry write protection for new context menu items changed to {enable}.", cancellationToken);

        return new PipeResponse
        {
            Success = true,
            Message = errors.Count == 0
                ? "Registry protection setting updated."
                : $"Registry protection setting updated. Some protected system roots were skipped.{Environment.NewLine}{string.Join(Environment.NewLine, errors)}",
            RegistryProtectionEnabled = enable
        };
    }

    /// <summary>
    /// Deletes item Async.
    /// </summary>
    public async Task<PipeResponse> DeleteItemAsync(
        string itemId,
        CancellationToken cancellationToken,
        BackendUserContext? userContext = null,
        ContextMenuEntry? fallbackItem = null)
        => await RunPersistentStateOperationAsync(
            () => DeleteItemCoreAsync(itemId, cancellationToken, userContext, fallbackItem),
            cancellationToken);

    private async Task<PipeResponse> DeleteItemCoreAsync(
        string itemId,
        CancellationToken cancellationToken,
        BackendUserContext? userContext,
        ContextMenuEntry? fallbackItem)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken, userContext);
        var item = snapshot.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = TryUseSceneFallbackItem(itemId, fallbackItem);
        }

        var states = await _stateStore.LoadAsync(cancellationToken);
        var persistedState = states.GetValueOrDefault(itemId);

        if (item is null && persistedState is null)
        {
            item = await TryFindEntryByIdAsync(itemId, cancellationToken, userContext);
            if (item is null)
            {
                return CreateFailure($"Menu item '{itemId}' was not found.");
            }
        }

        if (item is not null && item.IsDeleted)
        {
            return CreateFailure($"Menu item '{item.DisplayName}' is already deleted.", item);
        }

        if (item is not null && !item.IsPresentInRegistry)
        {
            return await RemoveMissingItemStateAsync(item, cancellationToken);
        }

        var deleteTarget = item ?? (persistedState is null ? null : CreateMinimalEntry(itemId, persistedState));
        if (deleteTarget is not null && IsProtectedFileTypeDeleteItem(deleteTarget))
        {
            return CreateFailure(
                $"'{deleteTarget.DisplayName}' is a protected file-type verb and cannot be deleted. Disable it instead.",
                deleteTarget);
        }

        try
        {
            var backendRegistryPath = item?.BackendRegistryPath ?? persistedState?.BackendRegistryPath;
            if (string.IsNullOrWhiteSpace(backendRegistryPath))
            {
                return CreateFailure($"Cannot delete '{itemId}': registry path is unknown.");
            }

            var backupFilePath = await _backupService.ExportKeyAsync(backendRegistryPath, cancellationToken);
            DeleteRegistryKey(backendRegistryPath);

            var state = GetOrCreateState(states, item ?? CreateMinimalEntry(itemId, persistedState!));
            state.DesiredEnabled = null;
            state.IsDeleted = true;
            state.IsPendingApproval = false;
            state.BackupFilePath = backupFilePath;
            state.DeletedAtUtc = DateTimeOffset.UtcNow;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _stateStore.SaveAsync(states, cancellationToken);
            ShellChangeNotifier.NotifyAssociationsChanged();

            await _logger.LogAsync($"Deleted {state.DisplayName} with backup {backupFilePath}.", cancellationToken);

            var refreshed = (await GetSnapshotAsync(cancellationToken, userContext))
                .FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase))
                ?? CreateVirtualEntry(state, null, ContextMenuChangeKind.None, null);

            return new PipeResponse
            {
                Success = true,
                Message = $"Deleted {state.DisplayName}.",
                Item = refreshed
            };
        }
        catch (Exception ex)
        {
            var displayName = item?.DisplayName ?? persistedState?.DisplayName ?? itemId;
            await _logger.LogAsync($"Failed to delete {displayName}: {ex.Message}", cancellationToken);
            return CreateFailure(ex.Message, item);
        }
    }

    private async Task<IReadOnlyList<ContextMenuEntry>> FindEntriesByIdAsync(
        string itemId,
        CancellationToken cancellationToken,
        BackendUserContext? userContext = null)
    {
        var separatorIndex = itemId.LastIndexOf('|');
        if (separatorIndex < 0)
        {
            return [];
        }

        var stableRelativePath = itemId[..separatorIndex];
        var keyName = itemId[(separatorIndex + 1)..];
        var candidates = new List<ContextMenuEntry>();

        var scope = userContext is null
            ? RegistryRootInstanceScope.AllKnownInstances
            : RegistryRootInstanceScope.MachineAndFrontendUser;

        foreach (var instance in EnumerateRootInstances(scope, userContext))
        {
            using var baseKey = instance.OpenBaseKey(stableRelativePath);
            if (baseKey is null)
            {
                continue;
            }

            using var itemKey = baseKey.OpenSubKey(keyName, writable: false);
            if (itemKey is null)
            {
                continue;
            }

            var category = DetermineCategoryFromPath(stableRelativePath);
            var entryKind = stableRelativePath.Contains(@"\shellex\", StringComparison.OrdinalIgnoreCase)
                ? ContextMenuEntryKind.ShellExtension
                : ContextMenuEntryKind.ShellVerb;
            var defaultValue = itemKey.GetValue(null)?.ToString();
            var handlerClsid = entryKind == ContextMenuEntryKind.ShellExtension
                ? ResolveShellExtensionHandlerClsid(keyName, defaultValue)
                : null;
            var displayName = ResolveDisplayName(
                new RegistryRootDescriptor(category, stableRelativePath, entryKind),
                itemKey,
                keyName,
                defaultValue,
                handlerClsid);
            var editableText = entryKind == ContextMenuEntryKind.ShellVerb
                ? ResolveEditableText(itemKey, defaultValue)
                : null;
            using var commandKey = entryKind == ContextMenuEntryKind.ShellVerb
                ? itemKey.OpenSubKey("command", writable: false)
                : null;
            var commandText = commandKey?.GetValue(null)?.ToString();
            var canEditCommandText = entryKind == ContextMenuEntryKind.ShellVerb
                && CanEditCommandText(itemKey, commandKey);

            var (iconPath, iconIndex) = entryKind switch
            {
                ContextMenuEntryKind.ShellVerb => ShellMetadataResolver.ResolveVerbIcon(itemKey, commandText),
                ContextMenuEntryKind.ShellExtension => ShellMetadataResolver.ResolveShellExtensionIcon(handlerClsid),
                _ => (null, 0)
            };

            var filePath = entryKind switch
            {
                ContextMenuEntryKind.ShellVerb => ShellMetadataResolver.ResolveVerbFilePath(itemKey, commandText),
                ContextMenuEntryKind.ShellExtension => ShellMetadataResolver.ResolveShellExtensionFilePath(handlerClsid),
                _ => null
            };

            iconPath = GuidMetadataCatalog.NormalizeCandidatePath(iconPath, filePath);

            var effectiveRelativePath = $@"{stableRelativePath}\{keyName}";
            var isEnabled = entryKind switch
            {
                ContextMenuEntryKind.ShellVerb => ShellVerbVisibility.IsEnabled(itemKey),
                ContextMenuEntryKind.ShellExtension => !IsDisabledContextMenuHandlersPath(effectiveRelativePath),
                _ => true
            };

            candidates.Add(new ContextMenuEntry
            {
                Id = itemId,
                Category = category,
                EntryKind = entryKind,
                KeyName = keyName,
                DisplayName = displayName,
                EditableText = editableText,
                RegistryPath = effectiveRelativePath,
                BackendRegistryPath = instance.ComposeAbsolutePath(effectiveRelativePath),
                SourceRootPath = stableRelativePath,
                CommandText = commandText,
                CanEditCommandText = canEditCommandText,
                CanToggle = entryKind != ContextMenuEntryKind.ShellExtension
                    || SupportsClassicShellExtensionContainerToggle(effectiveRelativePath),
                HandlerClsid = handlerClsid,
                IconPath = iconPath,
                IconIndex = iconIndex,
                FilePath = filePath,
                OnlyWithShift = entryKind == ContextMenuEntryKind.ShellVerb && itemKey.GetValue("Extended") is not null,
                OnlyInExplorer = entryKind == ContextMenuEntryKind.ShellVerb && itemKey.GetValue("OnlyInBrowserWindow") is not null,
                NoWorkingDirectory = entryKind == ContextMenuEntryKind.ShellVerb && itemKey.GetValue("NoWorkingDirectory") is not null,
                NeverDefault = entryKind == ContextMenuEntryKind.ShellVerb && itemKey.GetValue("NeverDefault") is not null,
                ShowAsDisabledIfHidden = entryKind == ContextMenuEntryKind.ShellVerb && itemKey.GetValue("ShowAsDisabledIfHidden") is not null,
                IsEnabled = isEnabled,
                IsPresentInRegistry = true,
                Notes = BuildNotes(entryKind, commandText, handlerClsid)
            });
        }

        await Task.CompletedTask;
        return candidates;
    }

    private async Task<ContextMenuEntry?> TryFindEntryByIdAsync(
        string itemId,
        CancellationToken cancellationToken,
        BackendUserContext? userContext = null)
        => SelectPreferredDeleteCandidate(
            await FindEntriesByIdAsync(itemId, cancellationToken, userContext));

    internal static ShellVerbMutationReconciliation ReconcileShellVerbMutation(
        ContextMenuEntry item,
        IReadOnlyList<ContextMenuEntry> physicalCandidates,
        ContextMenuEntry? refreshedLogicalEntry,
        bool requestedEnabled)
    {
        var matchingPhysicalCandidates = physicalCandidates
            .Where(candidate => candidate.EntryKind == ContextMenuEntryKind.ShellVerb
                                && string.Equals(candidate.Id, item.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var targetPathExists = matchingPhysicalCandidates.Any(candidate =>
            string.Equals(candidate.BackendRegistryPath, item.BackendRegistryPath, StringComparison.OrdinalIgnoreCase));
        var mismatchedPhysicalPaths = matchingPhysicalCandidates
            .Where(candidate => candidate.IsEnabled != requestedEnabled)
            .Select(static candidate => candidate.BackendRegistryPath)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var logicalMatchesRequest = refreshedLogicalEntry is null || refreshedLogicalEntry.IsEnabled == requestedEnabled;
        var failureReason = matchingPhysicalCandidates.Length == 0
            ? "No physical shell-verb candidate was found after mutation."
            : !targetPathExists
                ? "The physical shell-verb path targeted by the mutation no longer exists."
                : mismatchedPhysicalPaths.Length > 0
                    ? "One or more physical shell-verb candidates do not match the requested visibility state."
                    : !logicalMatchesRequest
                        ? "The refreshed logical candidate does not match the requested visibility state."
                        : null;
        var physicalEntry = SelectPreferredDeleteCandidate(matchingPhysicalCandidates);
        var entry = refreshedLogicalEntry ?? physicalEntry;

        return new ShellVerbMutationReconciliation(
            entry,
            matchingPhysicalCandidates.Length,
            refreshedLogicalEntry is null ? 0 : 1,
            targetPathExists,
            mismatchedPhysicalPaths,
            requestedEnabled,
            physicalEntry?.IsEnabled,
            refreshedLogicalEntry is null && physicalEntry is not null,
            failureReason);
    }

    internal sealed record ShellVerbMutationReconciliation(
        ContextMenuEntry? Entry,
        int MatchingPhysicalCandidateCount,
        int MatchingLogicalCandidateCount,
        bool TargetPathExists,
        IReadOnlyList<string> MismatchedPhysicalPaths,
        bool DesiredEnabled,
        bool? ObservedEnabled,
        bool UsedPhysicalSourceFallback,
        string? FailureReason)
    {
        public bool IsVerified => FailureReason is null && Entry is not null;

        public string MismatchedPhysicalPathsText => MismatchedPhysicalPaths.Count == 0
            ? "<none>"
            : string.Join(";", MismatchedPhysicalPaths);
    }

    private async Task<PipeResponse> RemoveMissingItemStateAsync(ContextMenuEntry item, CancellationToken cancellationToken)
    {
        var states = await _stateStore.LoadAsync(cancellationToken);
        if (states.Remove(item.Id))
        {
            PruneTransientStates(states);
            await _stateStore.SaveAsync(states, cancellationToken);
        }

        await _logger.LogAsync($"Removed missing item {item.DisplayName} from the catalog state.", cancellationToken);

        return new PipeResponse
        {
            Success = true,
            Message = $"Removed missing item {item.DisplayName} from the list.",
            Item = null
        };
    }

    private async Task<PipeResponse> RemovePendingApprovalItemAsync(
        ContextMenuEntry? item,
        string itemId,
        CancellationToken cancellationToken)
    {
        if (item is not null && item.IsPresentInRegistry && !item.IsDeleted)
        {
            return await DeleteItemAsync(itemId, cancellationToken);
        }

        return await RemovePendingApprovalStateAsync(itemId, cancellationToken);
    }

    private async Task<PipeResponse> RemovePendingApprovalStateAsync(string itemId, CancellationToken cancellationToken)
    {
        var states = await _stateStore.LoadAsync(cancellationToken);
        if (!states.TryGetValue(itemId, out var state))
        {
            return new PipeResponse
            {
                Success = true,
                Message = $"Approval item '{itemId}' is no longer present."
            };
        }

        var displayName = state.DisplayName;
        var deletedEntry = state.ToDeletedEntry();
        var shouldRemoveState = !state.IsDeleted && string.IsNullOrWhiteSpace(state.BackupFilePath);

        if (shouldRemoveState)
        {
            states.Remove(itemId);
        }
        else
        {
            state.IsPendingApproval = false;
            state.SuppressNextDetection = false;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        PruneTransientStates(states);
        await _stateStore.SaveAsync(states, cancellationToken);
        await _logger.LogAsync($"Removed {displayName} from the approval queue.", cancellationToken);

        return new PipeResponse
        {
            Success = true,
            Message = $"Removed {displayName} from the approval queue.",
            Item = shouldRemoveState ? null : deletedEntry with { IsPendingApproval = false }
        };
    }

    /// <summary>
    /// Executes undo Delete Async.
    /// </summary>
    public async Task<PipeResponse> UndoDeleteAsync(
        string itemId,
        CancellationToken cancellationToken,
        BackendUserContext? userContext = null)
        => await RunPersistentStateOperationAsync(
            () => UndoDeleteCoreAsync(itemId, cancellationToken, userContext),
            cancellationToken);

    private async Task<PipeResponse> UndoDeleteCoreAsync(
        string itemId,
        CancellationToken cancellationToken,
        BackendUserContext? userContext)
    {
        var states = await _stateStore.LoadAsync(cancellationToken);
        if (!states.TryGetValue(itemId, out var state) || !state.IsDeleted || string.IsNullOrWhiteSpace(state.BackupFilePath))
        {
            return CreateFailure($"No backup was found for '{itemId}'.");
        }

        try
        {
            if (RuntimePaths.PackageKind == RuntimePackageKind.Portable
                && (!_stateStore.IsCurrentHostIdentityVerified || !_backupService.IsCurrentHostBackupPath(state.BackupFilePath)))
            {
                await _logger.LogAsync(
                    RuntimeLogLevel.Warning,
                    $"BackupRestoreBlockedForeignHost: ItemId={itemId}, BackupFilePath={state.BackupFilePath}, StateStoreHostVerified={_stateStore.IsCurrentHostIdentityVerified}.",
                    cancellationToken);
                return CreateFailure(
                    "This backup belongs to a different Windows installation or user profile and cannot be restored safely.",
                    state.ToDeletedEntry("The backup file belongs to a different Windows installation or user profile."));
            }

            await _backupService.RestoreBackupAsync(state.BackupFilePath, cancellationToken);
            _backupService.DeleteBackupFile(state.BackupFilePath);

            state.IsDeleted = false;
            state.BackupFilePath = null;
            state.DeletedAtUtc = null;
            state.IsPendingApproval = false;
            state.SuppressNextDetection = true;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            state.DesiredEnabled = state.ObservedEnabled;
            PruneTransientStates(states);
            await _stateStore.SaveAsync(states, cancellationToken);
            ShellChangeNotifier.NotifyAssociationsChanged();

            await _logger.LogAsync($"Restored deleted item {state.DisplayName}.", cancellationToken);

            var refreshed = (await GetSnapshotAsync(cancellationToken, userContext))
                .FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase))
                ?? await TryFindEntryByIdAsync(itemId, cancellationToken, userContext);

            return new PipeResponse
            {
                Success = true,
                Message = refreshed is not null
                    ? $"Restored {refreshed.DisplayName}."
                    : $"The backup for {state.DisplayName} was restored, but the item could not be re-read.",
                Item = refreshed
            };
        }
        catch (Exception ex)
        {
            await _logger.LogAsync($"Failed to restore {state.DisplayName}: {ex.Message}", cancellationToken);
            return CreateFailure(ex.Message, state.ToDeletedEntry());
        }
    }

    /// <summary>
    /// Executes purge Deleted Item Async.
    /// </summary>
    public async Task<PipeResponse> PurgeDeletedItemAsync(string itemId, CancellationToken cancellationToken)
        => await RunPersistentStateOperationAsync(
            () => PurgeDeletedItemCoreAsync(itemId, cancellationToken),
            cancellationToken);

    private async Task<PipeResponse> PurgeDeletedItemCoreAsync(string itemId, CancellationToken cancellationToken)
    {
        var states = await _stateStore.LoadAsync(cancellationToken);
        if (!states.TryGetValue(itemId, out var state) || !state.IsDeleted)
        {
            return CreateFailure($"Deleted item '{itemId}' was not found.");
        }

        try
        {
            _backupService.DeleteBackupFile(state.BackupFilePath);
            states.Remove(itemId);
            await _stateStore.SaveAsync(states, cancellationToken);
            await _logger.LogAsync($"Permanently removed backup for {state.DisplayName}.", cancellationToken);

            return new PipeResponse
            {
                Success = true,
                Message = $"Permanently removed {state.DisplayName}."
            };
        }
        catch (Exception ex)
        {
            await _logger.LogAsync($"Failed to permanently remove {state.DisplayName}: {ex.Message}", cancellationToken);
            return CreateFailure(ex.Message, state.ToDeletedEntry());
        }
    }

    public async Task<PipeResponse> ResetStateDatabaseAsync(CancellationToken cancellationToken)
        => await RunPersistentStateOperationAsync(async () =>
        {
            await _stateStore.ResetAsync(cancellationToken);
            await _protectionSettingsStore.ResetAsync(cancellationToken);
            _backupService.ClearCurrentHostBackups();

            await _logger.LogAsync(
                "ContextMenuStateDatabaseReset: Next complete user-context snapshots will rebuild regular and WPS baselines.",
                cancellationToken);

            return new PipeResponse
            {
                Success = true,
                Message = "The local state database and deleted backups were reset."
            };
        }, cancellationToken);

    /// <summary>
    /// Executes mark Item Pending Approval Async.
    /// </summary>
    public async Task MarkItemPendingApprovalAsync(ContextMenuEntry item, CancellationToken cancellationToken)
        => await RunPersistentStateOperationAsync(
            () => MarkItemPendingApprovalCoreAsync(item, cancellationToken),
            cancellationToken);

    private async Task MarkItemPendingApprovalCoreAsync(ContextMenuEntry item, CancellationToken cancellationToken)
    {
        var states = await _stateStore.LoadAsync(cancellationToken);
        var state = GetOrCreateState(states, item);
        state.IsPendingApproval = true;
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _stateStore.SaveAsync(states, cancellationToken);
    }

    /// <summary>
    /// Executes quarantine New Item Async.
    /// </summary>
    public async Task<ContextMenuEntry> QuarantineNewItemAsync(ContextMenuEntry item, CancellationToken cancellationToken, BackendUserContext? userContext = null)
        => await RunPersistentStateOperationAsync(
            () => QuarantineNewItemCoreAsync(item, cancellationToken, userContext),
            cancellationToken);

    private async Task<ContextMenuEntry> QuarantineNewItemCoreAsync(
        ContextMenuEntry item,
        CancellationToken cancellationToken,
        BackendUserContext? userContext)
    {
        // Step 1: disable the newly detected item immediately. This keeps the
        // service in a deny-by-default posture until the user explicitly allows it.
        switch (item.EntryKind)
        {
            case ContextMenuEntryKind.ShellVerb when !item.IsWindows11ContextMenu:
                SetShellVerbEnabled(item.BackendRegistryPath, item.RegistryPath, enable: false);
                break;
            case ContextMenuEntryKind.ShellExtension when item.IsWindows11ContextMenu:
                if (!_windows11Catalog.SetEnabled(item.HandlerClsid ?? item.KeyName, item.DisplayName, userContext, enable: false))
                {
                    throw new InvalidOperationException($"Unable to quarantine the Win11 context menu item '{item.DisplayName}'.");
                }
                break;
            case ContextMenuEntryKind.ShellExtension:
                await SetShellExtensionEnabledAsync(item, enable: false, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unsupported entry kind: {item.EntryKind}");
        }

        var states = await _stateStore.LoadAsync(cancellationToken);
        var state = GetOrCreateState(states, item);

        // Step 2: persist the blocked state and mark it as waiting for approval.
        state.DesiredEnabled = false;
        state.ObservedEnabled = false;
        state.IsPendingApproval = true;
        state.PendingApprovalChangeKind = ContextMenuChangeKind.Added;
        state.IsDeleted = false;
        state.DeletedAtUtc = null;
        state.BackupFilePath = null;
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _stateStore.SaveAsync(states, cancellationToken);
        ShellChangeNotifier.NotifyAssociationsChanged();

        await _logger.LogAsync($"Quarantined new menu item pending approval: {item.DisplayName} ({item.RegistryPath}).", cancellationToken);

        return (await GetSnapshotAsync(cancellationToken, userContext))
            .FirstOrDefault(entry => string.Equals(entry.Id, item.Id, StringComparison.OrdinalIgnoreCase))
            ?? item with
            {
                IsEnabled = false,
                IsPendingApproval = true
            };
    }

    /// <summary>
    /// Disables a single registry entry using the per-entry-kind write path.
    /// This is the shared disable primitive used by reconciliation and new-item
    /// quarantine.
    /// </summary>
    private async Task DisableEntryCoreAsync(ContextMenuEntry item, BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        if (item.IsWindows11ContextMenu)
        {
            if (!_windows11Catalog.SetEnabled(item.HandlerClsid ?? item.KeyName, item.DisplayName, userContext, enable: false))
            {
                throw new InvalidOperationException($"Unable to disable the Win11 context menu item '{item.DisplayName}'.");
            }

            return;
        }

        switch (item.EntryKind)
        {
            case ContextMenuEntryKind.ShellVerb:
                SetShellVerbEnabled(item.BackendRegistryPath, item.RegistryPath, enable: false);
                break;
            case ContextMenuEntryKind.ShellExtension:
                await SetShellExtensionEnabledAsync(item, enable: false, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unsupported entry kind: {item.EntryKind}");
        }
    }

    /// <summary>
    /// Reconciles runtime disabled-to-enabled transitions selected by the
    /// monitor. The caller must pass only items that were observed disabled in
    /// the preceding settled runtime snapshot and are enabled now. This keeps
    /// startup/offline drift in the Modified workflow required by rule 5.
    /// </summary>
    public async Task<DisabledStateReconciliationResult> ReconcilePersistedDisabledItemsAsync(
        IReadOnlyList<ContextMenuEntry> snapshot,
        CancellationToken cancellationToken,
        BackendUserContext? userContext = null)
        => await RunPersistentStateOperationAsync(
            () => ReconcilePersistedDisabledItemsCoreAsync(snapshot, cancellationToken, userContext),
            cancellationToken);

    private async Task<DisabledStateReconciliationResult> ReconcilePersistedDisabledItemsCoreAsync(
        IReadOnlyList<ContextMenuEntry> snapshot,
        CancellationToken cancellationToken,
        BackendUserContext? userContext)
    {
        var states = await _stateStore.LoadAsync(cancellationToken);
        var reconciledItemIds = new List<string>();
        var failedItemIds = new List<string>();
        foreach (var entry in snapshot.Where(static e => e.IsPresentInRegistry && !e.IsDeleted))
        {
            if (!states.TryGetValue(entry.Id, out var state))
            {
                continue;
            }

            if (!ContextMenuChangeClassifier.ShouldReconcileDisabledState(entry, state))
            {
                continue;
            }

            try
            {
                await _logger.LogAsync(
                    $"DesiredStateDriftDetected: ItemId={entry.Id}, DesiredEnabled=False, ObservedEnabled=True.",
                    cancellationToken);

                await DisableEntryCoreAsync(entry, userContext, cancellationToken);

                state.ObservedEnabled = false;
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;

                reconciledItemIds.Add(entry.Id);

                await _logger.LogAsync(
                    $"DesiredStateReconciled: ItemId={entry.Id}, Result=Disabled, Reason=ExternalReenableOrRecreation.",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                await _logger.LogAsync(
                    RuntimeLogLevel.Warning,
                    $"DesiredStateReconciliationFailed: ItemId={entry.Id}, EntryKind={entry.EntryKind}, HandlerClsid={entry.HandlerClsid ?? "<none>"}, RequestedEnabled=False, RegistryPath={entry.RegistryPath}, BackendRegistryPath={entry.BackendRegistryPath}, Exception={ex.Message}.",
                    cancellationToken);
                failedItemIds.Add(entry.Id);
            }
        }

        if (reconciledItemIds.Count > 0)
        {
            await _stateStore.SaveAsync(states, cancellationToken);
            ShellChangeNotifier.NotifyAssociationsChanged();
        }

        return new DisabledStateReconciliationResult(
            reconciledItemIds.Count > 0,
            reconciledItemIds,
            failedItemIds);
    }
    /// <summary>
    /// Executes log Consistency Summary Async.
    /// </summary>
    public async Task<int> LogConsistencySummaryAsync(CancellationToken cancellationToken)
    {
        var inconsistencies = (await GetReadOnlySnapshotAsync(cancellationToken)).Count(static entry => entry.HasConsistencyIssue);
        await _logger.LogAsync($"Consistency check complete. Inconsistent items: {inconsistencies}.", cancellationToken);
        return inconsistencies;
    }

    /// <summary>
    /// Attempts to consume Suppressed Detection Async.
    /// </summary>
    public async Task<bool> TryConsumeSuppressedDetectionAsync(string itemId, CancellationToken cancellationToken)
        => await RunPersistentStateOperationAsync(
            () => TryConsumeSuppressedDetectionCoreAsync(itemId, cancellationToken),
            cancellationToken);

    private async Task<bool> TryConsumeSuppressedDetectionCoreAsync(string itemId, CancellationToken cancellationToken)
    {
        var states = await _stateStore.LoadAsync(cancellationToken);
        if (!states.TryGetValue(itemId, out var state) || !state.SuppressNextDetection)
        {
            return false;
        }

        state.SuppressNextDetection = false;
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _stateStore.SaveAsync(states, cancellationToken);
        return true;
    }

    private async Task<IReadOnlyList<ContextMenuEntry>> EnumerateActualEntriesAsync(CancellationToken cancellationToken, BackendUserContext? userContext = null)
    {
        var results = new List<ContextMenuEntry>();
        foreach (var item in EnumerateEntries(MonitoredRoots))
        {
            results.Add(item);
        }

        if (TryCreateRecycleBinPinToHomeEntry() is { } recycleBinPinToHomeEntry)
        {
            results.Add(recycleBinPinToHomeEntry);
        }

        if (_windows11Catalog.IsSupported)
        {
            results.AddRange(await _windows11Catalog.EnumerateEntriesAsync(cancellationToken, userContext));
        }

        return results;
    }

    private async Task<PipeResponse> AcknowledgeWpsOfficeSyntheticStateAsync(
        string itemId,
        ContextMenuEntry? item,
        CancellationToken cancellationToken)
    {
        var states = await _stateStore.LoadAsync(cancellationToken);
        if (item is not null)
        {
            var state = GetOrCreateState(states, item);
            state.IsPendingApproval = false;
            state.SuppressNextDetection = false;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _stateStore.SaveAsync(states, cancellationToken);
            await _logger.LogAsync($"Acknowledged WPS Office co-existence finding: {item.DisplayName} ({item.Id}).", cancellationToken);
            return new PipeResponse
            {
                Success = true,
                Message = $"Acknowledged {item.DisplayName}."
            };
        }

        if (states.TryGetValue(itemId, out var existing))
        {
            existing.IsPendingApproval = false;
            existing.SuppressNextDetection = false;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _stateStore.SaveAsync(states, cancellationToken);
            await _logger.LogAsync($"Acknowledged WPS Office co-existence finding: {existing.DisplayName} ({existing.Id}).", cancellationToken);
        }
        else
        {
            await _logger.LogAsync(
                RuntimeLogLevel.Warning,
                $"WPS Office co-existence finding state was missing during acknowledgement: {itemId}.",
                cancellationToken);
        }

        return new PipeResponse
        {
            Success = true,
            Message = $"Acknowledged WPS Office co-existence finding '{itemId}'."
        };
    }

    public OfficeSuiteCoexistenceStatus GetOfficeSuiteCoexistenceStatus(BackendUserContext? userContext)
        => _officeCoexistenceDetector.Detect(userContext);

    public async Task<PipeResponse> SetDocumentIconProviderAsync(
        BackendUserContext userContext,
        DocumentIconProvider provider,
        CancellationToken cancellationToken)
    {
        return await RunPersistentStateOperationAsync(async () =>
        {
            var response = _officeCoexistenceDetector.SetDocumentIconProvider(userContext, provider);
            if (!response.Success)
            {
                return response;
            }

            await RecordUserSelectedDocumentIconProviderCoreAsync(userContext, cancellationToken);
            return response;
        }, cancellationToken);
    }

    internal Task RecordUserSelectedDocumentIconProviderAsync(BackendUserContext userContext, CancellationToken cancellationToken)
        => RunPersistentStateOperationAsync(
            async () =>
            {
                await RecordUserSelectedDocumentIconProviderCoreAsync(userContext, cancellationToken);
                return true;
            },
            cancellationToken);

    private async Task RecordUserSelectedDocumentIconProviderCoreAsync(BackendUserContext userContext, CancellationToken cancellationToken)
    {
        var item = new ContextMenuEntry
        {
            Id = WpsOfficeDocumentIconSyntheticId,
            KeyName = "Document icons",
            DisplayName = "WPS changed document icons",
            RegistryPath = $@"HKEY_USERS\{userContext.Sid}\Software\Classes",
            BackendRegistryPath = $@"HKEY_USERS\{userContext.Sid}\Software\Classes",
            SourceRootPath = "special:wps-office-coexistence",
            IsEnabled = true,
            IsPresentInRegistry = true,
            DetectedChangeKind = ContextMenuChangeKind.WpsOfficeIconHijack
        };

        await AcknowledgeWpsOfficeSyntheticStateAsync(item.Id, item, cancellationToken);
        await _logger.LogAsync(
            $"WpsDocumentIconProviderUserSelectionAcknowledged: Sid={userContext.Sid}, ItemId={item.Id}. The document icon provider was changed through the frontend, so its WPS icon finding will not enter pending approval.",
            cancellationToken);
    }

    private IEnumerable<ContextMenuEntry> EnumerateEntries(IEnumerable<RegistryRootDescriptor> roots, BackendUserContext? userContext = null)
    {
        foreach (var root in roots)
        {
            foreach (var item in EnumerateRoot(root, userContext))
            {
                yield return item;
            }
        }
    }

    private IEnumerable<ContextMenuEntry> EnumerateRoot(RegistryRootDescriptor root, BackendUserContext? userContext = null)
    {
        foreach (var instance in EnumerateRootInstances(root.InstanceScope, userContext))
        {
            using var baseKey = instance.OpenBaseKey(root.RelativePath);
            if (baseKey is null)
            {
                continue;
            }

            foreach (var subKeyName in baseKey.GetSubKeyNames().OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
            {
                using var itemKey = baseKey.OpenSubKey(subKeyName, writable: false);
                if (itemKey is null)
                {
                    continue;
                }

                var defaultValue = itemKey.GetValue(null)?.ToString();
                var handlerClsid = root.EntryKind == ContextMenuEntryKind.ShellExtension
                    ? ResolveShellExtensionHandlerClsid(subKeyName, defaultValue)
                    : null;
                var displayName = ResolveDisplayName(root, itemKey, subKeyName, defaultValue, handlerClsid);
                var editableText = root.EntryKind == ContextMenuEntryKind.ShellVerb
                    ? ResolveEditableText(itemKey, defaultValue)
                    : null;
                var commandText = root.EntryKind == ContextMenuEntryKind.ShellVerb
                    ? itemKey.OpenSubKey("command", writable: false)?.GetValue(null)?.ToString()
                    : null;
                using var commandKey = root.EntryKind == ContextMenuEntryKind.ShellVerb
                    ? itemKey.OpenSubKey("command", writable: false)
                    : null;
                var canEditCommandText = root.EntryKind == ContextMenuEntryKind.ShellVerb
                    && CanEditCommandText(itemKey, commandKey);

                var (iconPath, iconIndex) = root.EntryKind switch
                {
                    ContextMenuEntryKind.ShellVerb => ShellMetadataResolver.ResolveVerbIcon(itemKey, commandText),
                    ContextMenuEntryKind.ShellExtension => ShellMetadataResolver.ResolveShellExtensionIcon(handlerClsid),
                    _ => (null, 0)
                };
                var filePath = root.EntryKind switch
                {
                    ContextMenuEntryKind.ShellVerb => ShellMetadataResolver.ResolveVerbFilePath(itemKey, commandText),
                    ContextMenuEntryKind.ShellExtension => ShellMetadataResolver.ResolveShellExtensionFilePath(handlerClsid),
                    _ => null
                };

                iconPath = GuidMetadataCatalog.NormalizeCandidatePath(iconPath, filePath);

                var effectiveRelativePath = $@"{root.RelativePath}\{subKeyName}";
                var isEnabled = root.EntryKind switch
                {
                    ContextMenuEntryKind.ShellVerb => ShellVerbVisibility.IsEnabled(itemKey),
                    ContextMenuEntryKind.ShellExtension => !root.IsDisabledContainer,
                    _ => true
                };

                yield return new ContextMenuEntry
                {
                    Id = $"{root.StableRelativePath}|{subKeyName}",
                    Category = root.Category,
                    EntryKind = root.EntryKind,
                    KeyName = subKeyName,
                    DisplayName = displayName,
                    EditableText = editableText,
                    RegistryPath = effectiveRelativePath,
                    BackendRegistryPath = instance.ComposeAbsolutePath(effectiveRelativePath),
                    SourceRootPath = root.StableRelativePath,
                    CommandText = commandText,
                    CanEditCommandText = canEditCommandText,
                    CanToggle = root.EntryKind != ContextMenuEntryKind.ShellExtension
                        || SupportsClassicShellExtensionContainerToggle(effectiveRelativePath),
                    HandlerClsid = handlerClsid,
                    IconPath = iconPath,
                    IconIndex = iconIndex,
                    FilePath = filePath,
                    OnlyWithShift = root.EntryKind == ContextMenuEntryKind.ShellVerb && itemKey.GetValue("Extended") is not null,
                    OnlyInExplorer = root.EntryKind == ContextMenuEntryKind.ShellVerb && itemKey.GetValue("OnlyInBrowserWindow") is not null,
                    NoWorkingDirectory = root.EntryKind == ContextMenuEntryKind.ShellVerb && itemKey.GetValue("NoWorkingDirectory") is not null,
                    NeverDefault = root.EntryKind == ContextMenuEntryKind.ShellVerb && itemKey.GetValue("NeverDefault") is not null,
                    ShowAsDisabledIfHidden = root.EntryKind == ContextMenuEntryKind.ShellVerb && itemKey.GetValue("ShowAsDisabledIfHidden") is not null,
                    IsEnabled = isEnabled,
                    IsPresentInRegistry = true,
                    Notes = BuildNotes(root.EntryKind, commandText, handlerClsid)
                };
            }
        }
    }

    private static string ResolveDisplayName(
        RegistryRootDescriptor root,
        RegistryKey itemKey,
        string fallbackKeyName,
        string? rawDefaultValue,
        string? handlerClsid)
    {
        var displayName = itemKey.Name.Contains(@"\shellex\", StringComparison.OrdinalIgnoreCase)
            ? ShellMetadataResolver.ResolveShellExtensionDisplayName(fallbackKeyName, handlerClsid, rawDefaultValue)
            : ShellMetadataResolver.ResolveVerbDisplayName(itemKey, fallbackKeyName);

        if (itemKey.Name.Contains(@"\shellex\", StringComparison.OrdinalIgnoreCase)
            && string.Equals(displayName, fallbackKeyName, StringComparison.Ordinal)
            && Guid.TryParse(fallbackKeyName, out _)
            && !string.IsNullOrWhiteSpace(rawDefaultValue)
            && !Guid.TryParse(rawDefaultValue, out _))
        {
            displayName = rawDefaultValue;
        }

        if (root.Category == ContextMenuCategory.RecycleBin
            && root.RelativePath.EndsWith(@"\shellex\PropertySheetHandlers", StringComparison.OrdinalIgnoreCase))
        {
            displayName = "Properties";
        }

        return NormalizeDisplayName(displayName);
    }

    private static string? ResolveShellExtensionHandlerClsid(string keyName, string? defaultValue)
    {
        if (Guid.TryParse(defaultValue, out var defaultGuid))
        {
            return defaultGuid.ToString("B");
        }

        if (Guid.TryParse(keyName, out var keyGuid))
        {
            return keyGuid.ToString("B");
        }

        return string.IsNullOrWhiteSpace(defaultValue)
            ? null
            : defaultValue.Trim();
    }

    private static string NormalizeDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return string.Empty;
        }

        const string escapedAmpersandToken = "\uF000";
        var normalized = displayName
            .Replace("&&", escapedAmpersandToken, StringComparison.Ordinal)
            .Replace("&", string.Empty, StringComparison.Ordinal)
            .Replace(escapedAmpersandToken, "&", StringComparison.Ordinal)
            .Trim();

        return string.IsNullOrWhiteSpace(normalized)
            ? displayName.Trim()
            : normalized;
    }

    private static string? BuildNotes(ContextMenuEntryKind kind, string? commandText, string? handlerClsid)
    {
        return kind switch
        {
            ContextMenuEntryKind.ShellVerb when !string.IsNullOrWhiteSpace(commandText) => commandText,
            ContextMenuEntryKind.ShellExtension when !string.IsNullOrWhiteSpace(handlerClsid) => $"Handler CLSID: {handlerClsid}",
            _ => null
        };
    }

    private static string? GetConsistencyIssue(
        ContextMenuEntry entry,
        PersistedContextMenuState? state,
        bool hasLegacyGlobalShellExtensionBlock = false)
        => !string.IsNullOrWhiteSpace(entry.ConsistencyIssue)
            ? entry.ConsistencyIssue
            : GetLegacyGlobalShellExtensionBlockConsistencyIssue(entry, hasLegacyGlobalShellExtensionBlock)
                ?? ContextMenuChangeClassifier.GetConsistencyIssue(entry, state);

    internal static string? GetLegacyGlobalShellExtensionBlockConsistencyIssue(
        ContextMenuEntry entry,
        bool hasLegacyGlobalShellExtensionBlock)
    {
        if (!hasLegacyGlobalShellExtensionBlock
            || entry.IsWindows11ContextMenu
            || entry.EntryKind != ContextMenuEntryKind.ShellExtension
            || !entry.IsEnabled)
        {
            return null;
        }

        return "The handler CLSID is also in the legacy global Shell Extensions\\Blocked list.";
    }

    private static bool HasLegacyGlobalShellExtensionBlock(ContextMenuEntry entry)
    {
        if (entry.IsWindows11ContextMenu
            || entry.EntryKind != ContextMenuEntryKind.ShellExtension
            || !entry.IsEnabled
            || !Guid.TryParse(entry.HandlerClsid, out var handlerGuid))
        {
            return false;
        }

        try
        {
            using var blockedKey = Registry.LocalMachine.OpenSubKey(LegacyGlobalShellExtensionsBlockedPath, writable: false);
            return blockedKey?.GetValueNames()
                .Any(valueName => Guid.TryParse(valueName, out var blockedGuid) && blockedGuid == handlerGuid)
                ?? false;
        }
        catch (SecurityException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ContextMenuChangeKind GetDetectedChangeKind(ContextMenuEntry entry, PersistedContextMenuState? state, bool hasBaseline)
        => ContextMenuChangeClassifier.GetDetectedChangeKind(entry, state, hasBaseline);

    private static string? GetDetectedChangeDetails(ContextMenuEntry entry, PersistedContextMenuState? state, ContextMenuChangeKind changeKind)
        => ContextMenuChangeClassifier.GetDetectedChangeDetails(entry, state, changeKind);

    private string? GetDeletedConsistencyIssue(PersistedContextMenuState state)
    {
        if (string.IsNullOrWhiteSpace(state.BackupFilePath) || !File.Exists(state.BackupFilePath))
        {
            return "The backup file for this deleted item is missing.";
        }

        if (RuntimePaths.PackageKind == RuntimePackageKind.Portable
            && !_backupService.IsCurrentHostBackupPath(state.BackupFilePath))
        {
            return "The backup file belongs to a different Windows installation or user profile.";
        }

        return null;
    }

    private static bool UpdateMetadata(PersistedContextMenuState state, ContextMenuEntry entry)
    {
        var dirty = false;

        dirty |= UpdateIfChanged(state.Category, entry.Category, value => state.Category = value);
        dirty |= UpdateIfChanged(state.DisplayName, entry.DisplayName, value => state.DisplayName = value);
        dirty |= UpdateIfChanged(state.EditableText, entry.EditableText, value => state.EditableText = value);
        dirty |= UpdateIfChanged(state.RegistryPath, entry.RegistryPath, value => state.RegistryPath = value);
        dirty |= UpdateIfChanged(state.BackendRegistryPath, entry.BackendRegistryPath, value => state.BackendRegistryPath = value);
        dirty |= UpdateIfChanged(state.SourceRootPath, entry.SourceRootPath, value => state.SourceRootPath = value);
        dirty |= UpdateIfChanged(state.CommandText, entry.CommandText, value => state.CommandText = value);
        dirty |= UpdateIfChanged(state.HandlerClsid, entry.HandlerClsid, value => state.HandlerClsid = value);
        dirty |= UpdateIfChanged(state.IconPath, entry.IconPath, value => state.IconPath = value);
        dirty |= UpdateIfChanged(state.IconIndex, entry.IconIndex, value => state.IconIndex = value);
        dirty |= UpdateIfChanged(state.FilePath, entry.FilePath, value => state.FilePath = value);
        dirty |= UpdateIfChanged(state.IsWindows11ContextMenu, entry.IsWindows11ContextMenu, value => state.IsWindows11ContextMenu = value);
        dirty |= UpdateIfChanged(state.Windows11SourceKind, entry.Windows11SourceKind, value => state.Windows11SourceKind = value);
        dirty |= UpdateIfChanged(state.IsProtectedSystemItem, entry.IsProtectedSystemItem, value => state.IsProtectedSystemItem = value);
        dirty |= UpdateIfChanged(state.OnlyWithShift, entry.OnlyWithShift, value => state.OnlyWithShift = value);
        dirty |= UpdateIfChanged(state.OnlyInExplorer, entry.OnlyInExplorer, value => state.OnlyInExplorer = value);
        dirty |= UpdateIfChanged(state.NoWorkingDirectory, entry.NoWorkingDirectory, value => state.NoWorkingDirectory = value);
        dirty |= UpdateIfChanged(state.NeverDefault, entry.NeverDefault, value => state.NeverDefault = value);
        dirty |= UpdateIfChanged(state.ShowAsDisabledIfHidden, entry.ShowAsDisabledIfHidden, value => state.ShowAsDisabledIfHidden = value);
        dirty |= UpdateIfChanged(state.Notes, entry.Notes, value => state.Notes = value);
        dirty |= UpdateIfChanged(state.ObservedEnabled, entry.IsEnabled, value => state.ObservedEnabled = value);

        if (dirty)
        {
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        return dirty;
    }

    private static bool UpdateIfChanged<T>(T current, T updated, Action<T> setter)
    {
        if (EqualityComparer<T>.Default.Equals(current, updated))
        {
            return false;
        }

        setter(updated);
        return true;
    }

    private static ContextMenuEntry CreateVirtualEntry(
        PersistedContextMenuState state,
        string? issue,
        ContextMenuChangeKind changeKind,
        string? changeDetails)
    {
        if (state.IsDeleted)
        {
            return state.ToDeletedEntry(issue);
        }

        return new ContextMenuEntry
        {
            Id = state.Id,
            Category = state.Category,
            EntryKind = state.EntryKind,
            KeyName = state.KeyName,
            DisplayName = state.DisplayName,
            EditableText = state.EditableText,
            RegistryPath = state.RegistryPath,
            BackendRegistryPath = state.BackendRegistryPath,
            SourceRootPath = state.SourceRootPath,
            CommandText = state.CommandText,
            HandlerClsid = state.HandlerClsid,
            IconPath = state.IconPath,
            IconIndex = state.IconIndex,
            FilePath = state.FilePath,
            IsWindows11ContextMenu = state.IsWindows11ContextMenu,
            Windows11SourceKind = state.Windows11SourceKind,
            IsProtectedSystemItem = state.IsProtectedSystemItem,
            OnlyWithShift = state.OnlyWithShift,
            OnlyInExplorer = state.OnlyInExplorer,
            NoWorkingDirectory = state.NoWorkingDirectory,
            NeverDefault = state.NeverDefault,
            ShowAsDisabledIfHidden = state.ShowAsDisabledIfHidden,
            IsPresentInRegistry = false,
            IsEnabled = state.DesiredEnabled ?? true,
            Notes = state.Notes,
            IsDeleted = false,
            IsPendingApproval = state.IsPendingApproval,
            HasBackup = !string.IsNullOrWhiteSpace(state.BackupFilePath),
            DeletedAtUtc = state.DeletedAtUtc,
            DetectedChangeKind = changeKind,
            DetectedChangeDetails = changeDetails,
            HasConsistencyIssue = !string.IsNullOrWhiteSpace(issue),
            ConsistencyIssue = issue
        };
    }

    private static ContextMenuEntry CreateMinimalEntry(string itemId, PersistedContextMenuState state)
    {
        return new ContextMenuEntry
        {
            Id = itemId,
            Category = state.Category,
            EntryKind = state.EntryKind,
            KeyName = state.KeyName,
            DisplayName = state.DisplayName,
            EditableText = state.EditableText,
            RegistryPath = state.RegistryPath,
            BackendRegistryPath = state.BackendRegistryPath,
            SourceRootPath = state.SourceRootPath,
            CommandText = state.CommandText,
            HandlerClsid = state.HandlerClsid,
            IconPath = state.IconPath,
            IconIndex = state.IconIndex,
            FilePath = state.FilePath,
            IsWindows11ContextMenu = state.IsWindows11ContextMenu,
            Windows11SourceKind = state.Windows11SourceKind,
            IsProtectedSystemItem = state.IsProtectedSystemItem,
            OnlyWithShift = state.OnlyWithShift,
            OnlyInExplorer = state.OnlyInExplorer,
            NoWorkingDirectory = state.NoWorkingDirectory,
            NeverDefault = state.NeverDefault,
            ShowAsDisabledIfHidden = state.ShowAsDisabledIfHidden,
            IsPresentInRegistry = true,
            IsEnabled = state.DesiredEnabled ?? true,
            Notes = state.Notes
        };
    }

    private static PersistedContextMenuState GetOrCreateState(
        IDictionary<string, PersistedContextMenuState> states,
        ContextMenuEntry entry)
    {
        if (states.TryGetValue(entry.Id, out var existing))
        {
            UpdateMetadata(existing, entry);
            return existing;
        }

        var state = PersistedContextMenuState.FromEntry(entry);
        states[entry.Id] = state;
        return state;
    }

    private static ContextMenuEntry? TryUseSceneFallbackItem(string itemId, ContextMenuEntry? fallbackItem)
    {
        if (fallbackItem is null
            || !string.Equals(fallbackItem.Id, itemId, StringComparison.OrdinalIgnoreCase)
            || fallbackItem.IsDeleted
            || !fallbackItem.IsPresentInRegistry
            || string.IsNullOrWhiteSpace(fallbackItem.BackendRegistryPath)
            || string.IsNullOrWhiteSpace(fallbackItem.RegistryPath)
            || string.IsNullOrWhiteSpace(fallbackItem.SourceRootPath))
        {
            return null;
        }

        if (fallbackItem.EntryKind is not ContextMenuEntryKind.ShellVerb
            and not ContextMenuEntryKind.ShellExtension)
        {
            return null;
        }

        using var itemKey = OpenRegistryKey(fallbackItem.BackendRegistryPath, writable: false);
        if (itemKey is null)
        {
            return null;
        }

        if (fallbackItem.EntryKind == ContextMenuEntryKind.ShellExtension)
        {
            if (string.IsNullOrWhiteSpace(fallbackItem.HandlerClsid))
            {
                return null;
            }

            var actualHandlerClsid = ResolveShellExtensionHandlerClsid(
                fallbackItem.KeyName,
                itemKey.GetValue(null)?.ToString());

            if (!string.Equals(actualHandlerClsid, fallbackItem.HandlerClsid, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return fallbackItem;
    }

    internal static IReadOnlyList<ContextMenuEntry> GetStateLinkedEntries(
        IReadOnlyList<ContextMenuEntry> snapshot,
        ContextMenuEntry item)
    {
        // A classic shell extension is controlled per registration. A shared CLSID
        // is only a Windows global-block identity, never a reason to link classic
        // category switches or persisted state. Packaged Windows 11 entries retain
        // their existing global blocked-list control domain.
        if (!item.IsWindows11ContextMenu || string.IsNullOrWhiteSpace(item.HandlerClsid))
        {
            return [item];
        }

        return snapshot
            .Where(entry => entry.IsWindows11ContextMenu
                            && string.Equals(entry.HandlerClsid, item.HandlerClsid, StringComparison.OrdinalIgnoreCase))
            .Append(item)
            .GroupBy(static entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
    }

    private static void PruneTransientStates(IDictionary<string, PersistedContextMenuState> states)
    {
        foreach (var staleId in states
                     .Where(static pair => ShouldPruneTransientState(pair.Value))
                     .Select(static pair => pair.Key)
                     .ToList())
        {
            states.Remove(staleId);
        }
    }

    private static bool ShouldPruneTransientState(PersistedContextMenuState state)
    {
        // Keep the long-lived monitoring baseline for the real monitored roots,
        // but drop neutral leftovers from scene pages or previous buggy versions
        // so they do not pollute future startup comparisons.
        if (MonitoredStableRootPaths.Contains(state.SourceRootPath))
        {
            return false;
        }

        if (state.IsWindows11ContextMenu
            || string.Equals(state.SourceRootPath, Windows11MonitoredRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsWpsOfficeSyntheticSource(state.SourceRootPath))
        {
            return false;
        }

        return !state.IsDeleted
               && !state.IsPendingApproval
               && !state.SuppressNextDetection
               && state.DesiredEnabled is null
               && string.IsNullOrWhiteSpace(state.BackupFilePath);
    }

    private static ContextMenuEntry? TryCreateRecycleBinPinToHomeEntry()
    {
        using var itemKey = OpenRegistryKey(RecycleBinPinToHomeRegistryPath, writable: false);
        if (itemKey is null)
        {
            return null;
        }

        var displayName = ShellMetadataResolver.ResolveVerbDisplayName(itemKey, "pintohome");
        if (string.IsNullOrWhiteSpace(displayName) || string.Equals(displayName, "pintohome", StringComparison.OrdinalIgnoreCase))
        {
            displayName = "RecycleBinPinToQuickAccess";
        }

        var appliesTo = itemKey.GetValue("AppliesTo")?.ToString();
        var isEnabled = !ContainsRecycleBinParsingNameExclusion(appliesTo);

        return new ContextMenuEntry
        {
            Id = RecycleBinPinToHomeId,
            Category = ContextMenuCategory.RecycleBin,
            EntryKind = ContextMenuEntryKind.ShellVerb,
            KeyName = "pintohome",
            DisplayName = NormalizeDisplayName(displayName),
            EditableText = NormalizeDisplayName(displayName),
            RegistryPath = @"Folder\shell\pintohome",
            BackendRegistryPath = RecycleBinPinToHomeRegistryPath,
            SourceRootPath = RecycleBinPinToHomeSourceRootPath,
            IsEnabled = isEnabled,
            IsPresentInRegistry = true,
            Notes = "Controls whether the Recycle Bin exposes the Folder\\shell\\pintohome verb."
        };
    }

    private async Task<PipeResponse> ApplyRecycleBinPinToHomeStateAsync(
        bool enable,
        CancellationToken cancellationToken,
        BackendUserContext? userContext)
    {
        var item = TryCreateRecycleBinPinToHomeEntry();
        if (item is null)
        {
            return CreateFailure("Recycle Bin 'Pin to Quick access' registry key was not found.");
        }

        try
        {
            using var menuKey = OpenRegistryKey(RecycleBinPinToHomeRegistryPath, writable: true)
                ?? throw new InvalidOperationException($"Unable to open {RecycleBinPinToHomeRegistryPath} for writing.");

            var existingAppliesTo = menuKey.GetValue("AppliesTo")?.ToString();
            var nextAppliesTo = enable
                ? RemoveRecycleBinParsingNameExclusion(existingAppliesTo)
                : AddRecycleBinParsingNameExclusion(existingAppliesTo);

            if (string.IsNullOrWhiteSpace(nextAppliesTo))
            {
                menuKey.DeleteValue("AppliesTo", throwOnMissingValue: false);
            }
            else
            {
                menuKey.SetValue("AppliesTo", nextAppliesTo, RegistryValueKind.String);
            }

            var states = await _stateStore.LoadAsync(cancellationToken);
            var state = GetOrCreateState(states, item);
            state.DesiredEnabled = enable;
            state.ObservedEnabled = enable;
            state.IsDeleted = false;
            state.IsPendingApproval = false;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            state.DeletedAtUtc = null;
            state.BackupFilePath = null;
            await _stateStore.SaveAsync(states, cancellationToken);

            ShellChangeNotifier.NotifyAssociationsChanged();
            var refreshed = TryCreateRecycleBinPinToHomeEntry() ?? item with { IsEnabled = enable };
            return new PipeResponse
            {
                Success = true,
                Message = $"{(enable ? "Enabled" : "Disabled")} {refreshed.DisplayName}.",
                Item = refreshed
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Warning, $"Permission denied when updating Recycle Bin Pin to Quick access. Sid={DiagnosticLogFormatter.FormatSid(userContext)}, Error={ex}", cancellationToken);
            return CreateFailure("Access denied while updating Recycle Bin 'Pin to Quick access'.", item);
        }
        catch (SecurityException ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Warning, $"Security error when updating Recycle Bin Pin to Quick access. Sid={DiagnosticLogFormatter.FormatSid(userContext)}, Error={ex}", cancellationToken);
            return CreateFailure("Access denied while updating Recycle Bin 'Pin to Quick access'.", item);
        }
        catch (Exception ex)
        {
            await _logger.LogAsync($"Failed to update Recycle Bin Pin to Quick access: {ex}", cancellationToken);
            return CreateFailure(ex.Message, item);
        }
    }

    private static bool ContainsRecycleBinParsingNameExclusion(string? appliesTo)
        => !string.IsNullOrWhiteSpace(appliesTo)
           && appliesTo.Contains(RecycleBinParsingNameExclusion, StringComparison.OrdinalIgnoreCase);

    private static string AddRecycleBinParsingNameExclusion(string? appliesTo)
    {
        if (string.IsNullOrWhiteSpace(appliesTo))
        {
            return RecycleBinParsingNameExclusion;
        }

        if (ContainsRecycleBinParsingNameExclusion(appliesTo))
        {
            return appliesTo.Trim();
        }

        return $"{appliesTo.Trim()} AND {RecycleBinParsingNameExclusion}";
    }

    private static string? RemoveRecycleBinParsingNameExclusion(string? appliesTo)
    {
        if (string.IsNullOrWhiteSpace(appliesTo))
        {
            return appliesTo;
        }

        var updated = Regex.Replace(
            appliesTo,
            $@"\s+AND\s+{Regex.Escape(RecycleBinParsingNameExclusion)}|{Regex.Escape(RecycleBinParsingNameExclusion)}\s+AND\s+|{Regex.Escape(RecycleBinParsingNameExclusion)}",
            string.Empty,
            RegexOptions.IgnoreCase);

        return string.IsNullOrWhiteSpace(updated) ? null : updated.Trim();
    }

    private void SetShellVerbEnabled(string registryPath, string displayRegistryPath, bool enable)
    {
        try
        {
            using var menuKey = OpenRegistryKey(registryPath, writable: true)
                ?? throw new InvalidOperationException($"Unable to open {registryPath} for writing.");
            ApplyAndVerifyShellVerbVisibility(menuKey, displayRegistryPath, enable);
            return;
        }
        catch (Exception ex) when (IsRegistryAccessDenied(ex) && ProtectedRegistryMutation.IsEligibleMachineClassesPath(registryPath))
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"ProtectedShellVerbFallbackStarted: BackendRegistryPath={registryPath}, Enable={enable}, InitialException={ex.GetType().Name}: {ex.Message}");
            ProtectedRegistryMutation.Execute(
                registryPath,
                key => ShellVerbVisibility.SetEnabled(key, displayRegistryPath, enable),
                key => VerifyShellVerbVisibility(key, enable));
            _logger.LogFireAndForget($"ProtectedShellVerbFallbackSucceeded: BackendRegistryPath={registryPath}, Enable={enable}, SecurityDescriptorRestored=True.");
        }
    }

    private static void ApplyAndVerifyShellVerbVisibility(RegistryKey menuKey, string displayRegistryPath, bool enable)
    {
        ShellVerbVisibility.SetEnabled(menuKey, displayRegistryPath, enable);
        VerifyShellVerbVisibility(menuKey, enable);
    }

    private static void VerifyShellVerbVisibility(RegistryKey menuKey, bool expectedEnabled)
    {
        if (ShellVerbVisibility.IsEnabled(menuKey) != expectedEnabled)
        {
            throw new ProtectedRegistryMutationException(
                PipeErrorCodes.RegistryMutationVerificationFailed,
                "The requested shell verb visibility change could not be verified.");
        }
    }

    private static bool IsRegistryAccessDenied(Exception exception)
        => exception is UnauthorizedAccessException or SecurityException;

    private static void SetShellVerbAttribute(string registryPath, ContextMenuShellAttribute attribute, bool enable)
    {
        using var menuKey = OpenRegistryKey(registryPath, writable: true)
            ?? throw new InvalidOperationException($"Unable to open {registryPath} for writing.");

        var valueName = attribute switch
        {
            ContextMenuShellAttribute.OnlyWithShift => "Extended",
            ContextMenuShellAttribute.OnlyInExplorer => "OnlyInBrowserWindow",
            ContextMenuShellAttribute.NoWorkingDirectory => "NoWorkingDirectory",
            ContextMenuShellAttribute.NeverDefault => "NeverDefault",
            ContextMenuShellAttribute.ShowAsDisabledIfHidden => "ShowAsDisabledIfHidden",
            _ => throw new InvalidOperationException($"Unsupported shell attribute: {attribute}")
        };

        if (enable)
        {
            menuKey.SetValue(valueName, string.Empty, RegistryValueKind.String);
        }
        else
        {
            menuKey.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    private async Task SetShellExtensionEnabledAsync(ContextMenuEntry item, bool enable, CancellationToken cancellationToken)
    {
        if (item.IsWindows11ContextMenu)
        {
            throw new InvalidOperationException("Windows 11 context menu entries must use the Windows 11 blocked-list path.");
        }

        if (!item.CanToggle || !SupportsClassicShellExtensionContainerToggle(item.BackendRegistryPath))
        {
            throw new InvalidOperationException(
                $"'{item.RegistryPath}' is not a classic ContextMenuHandlers registration and has no verified per-registration toggle operation.");
        }

        // A classic Shell Extension logical item can be backed by several
        // physical registrations that share the same stable Id (for example a
        // machine-level HKLM copy and a per-user HKEY_USERS copy). Moving only
        // the snapshot's preferred physical registration would leave the other
        // copies active and make the logical item appear unchanged after the
        // post-mutation refresh (REGISTRY_MUTATION_VERIFICATION_FAILED), so
        // every physical copy that shares the Id is moved together.
        var physicalEntries = ResolvePhysicalShellExtensionEntries(item, EnumerateEntries(MonitoredRoots));

        foreach (var physical in physicalEntries)
        {
            await MoveShellExtensionRegistrationAsync(physical, enable, cancellationToken);
        }
    }

    /// <summary>
    /// Resolves every physical registration that must be toggled for a logical
    /// classic Shell Extension item. A single logical Id can be backed by
    /// several physical registrations (machine-level HKLM and per-user
    /// HKEY_USERS copies, plus the active and disabled mirror containers), and
    /// all of them must move together or the item would still appear enabled
    /// after the mutation. When no candidate matches (scene / fallback items
    /// that are not re-enumerable through the standard monitored roots), the
    /// caller's entry is returned unchanged to preserve single-registration
    /// behavior.
    /// </summary>
    internal static IReadOnlyList<ContextMenuEntry> ResolvePhysicalShellExtensionEntries(
        ContextMenuEntry item,
        IEnumerable<ContextMenuEntry> physicalCandidates)
    {
        var matches = physicalCandidates
            .Where(entry => entry.EntryKind == ContextMenuEntryKind.ShellExtension
                            && string.Equals(entry.Id, item.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length == 0 ? [item] : matches;
    }

    private async Task MoveShellExtensionRegistrationAsync(ContextMenuEntry item, bool enable, CancellationToken cancellationToken)
    {
        var sourcePath = item.BackendRegistryPath;
        var sourceIsDisabled = IsDisabledContextMenuHandlersPath(sourcePath);
        if (enable == !sourceIsDisabled)
        {
            return;
        }

        var destinationPath = GetSiblingContextMenuHandlersPath(sourcePath, enable);

        string? ReadPhysicalClsid(string path)
        {
            using var key = OpenRegistryKey(path, writable: false);
            var rawDefault = key?.GetValue(null)?.ToString();
            return string.IsNullOrWhiteSpace(rawDefault)
                ? null
                : ResolveShellExtensionHandlerClsid(Path.GetFileName(path), rawDefault);
        }

        var sourceExistedBefore = OpenRegistryKey(sourcePath, writable: false) is not null;
        var destinationExistedBefore = OpenRegistryKey(destinationPath, writable: false) is not null;
        var sourceClsid = ReadPhysicalClsid(sourcePath);
        var destinationClsid = ReadPhysicalClsid(destinationPath);

        await _logger.LogAsync(
            $"ClassicShellExtensionMoveStarted: ItemId={item.Id}, Category={item.Category}, EntryKind={item.EntryKind}, HandlerClsid={item.HandlerClsid ?? "<none>"}, RequestedEnabled={enable}, RegistryPath={item.RegistryPath}, BackendRegistryPath={item.BackendRegistryPath}, Source={sourcePath}, Destination={destinationPath}, SourceExistedBefore={sourceExistedBefore}, DestinationExistedBefore={destinationExistedBefore}, SourceClsid={sourceClsid ?? "<none>"}, DestinationClsid={destinationClsid ?? "<none>"}.",
            cancellationToken);

        MoveRegistryKeySafely(sourcePath, destinationPath);

        var sourceExistedAfter = OpenRegistryKey(sourcePath, writable: false) is not null;
        var destinationExistedAfter = OpenRegistryKey(destinationPath, writable: false) is not null;
        var finalClsid = ReadPhysicalClsid(destinationPath);

        await _logger.LogAsync(
            $"ClassicShellExtensionMoveSucceeded: ItemId={item.Id}, Category={item.Category}, EntryKind={item.EntryKind}, HandlerClsid={item.HandlerClsid ?? "<none>"}, RequestedEnabled={enable}, RegistryPath={item.RegistryPath}, BackendRegistryPath={item.BackendRegistryPath}, Source={sourcePath}, Destination={destinationPath}, SourceExistedBefore={sourceExistedBefore}, DestinationExistedBefore={destinationExistedBefore}, SourceExistedAfter={sourceExistedAfter}, DestinationExistedAfter={destinationExistedAfter}, DestinationClsid={finalClsid ?? "<none>"}.",
            cancellationToken);
    }

    internal static string GetSiblingContextMenuHandlersPath(string sourcePath, bool enable)
    {
        var sourceIsDisabled = IsDisabledContextMenuHandlersPath(sourcePath);
        if (enable)
        {
            if (!sourceIsDisabled)
            {
                return sourcePath;
            }

            return sourcePath.Replace(@"\shellex\-ContextMenuHandlers\", @"\shellex\ContextMenuHandlers\", StringComparison.OrdinalIgnoreCase);
        }

        if (sourceIsDisabled)
        {
            return sourcePath;
        }

        if (!sourcePath.Contains(@"\shellex\ContextMenuHandlers\", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"'{sourcePath}' is not a classic ContextMenuHandlers registration path.");
        }

        return sourcePath.Replace(@"\shellex\ContextMenuHandlers\", @"\shellex\-ContextMenuHandlers\", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsDisabledContextMenuHandlersPath(string path)
        => path.Contains(@"\shellex\-ContextMenuHandlers\", StringComparison.OrdinalIgnoreCase);

    internal static bool SupportsClassicShellExtensionContainerToggle(string path)
        => path.Contains(@"\shellex\ContextMenuHandlers\", StringComparison.OrdinalIgnoreCase)
           || IsDisabledContextMenuHandlersPath(path);

    internal static void MoveRegistryKeySafely(string sourcePath, string destinationPath)
    {
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var existingDestination = OpenRegistryKey(destinationPath, writable: false);
        if (existingDestination is not null)
        {
            // The destination already exists. Two situations are possible:
            // 1. A duplicate/recreated copy of an already-disabled registration
            //    whose registry tree is equivalent to the source. This happens
            //    when a third-party application recreates the active copy while
            //    ContextMenuMgr keeps the disabled copy. The requested state can
            //    be reconciled by removing only the redundant source.
            // 2. A genuinely conflicting registration (different CLSID or
            //    different registry tree). Both registrations must be preserved
            //    and the operation must fail safely.
            using var source = OpenRegistryKey(sourcePath, writable: false)
                ?? throw new InvalidOperationException($"Source shell-extension registration was not found: {sourcePath}.");

            if (RegistryKeyTreesEquivalent(source, existingDestination))
            {
                var destinationDefaultValue = existingDestination.GetValue(null)?.ToString();

                // Remove only the redundant source. The existing destination is
                // never deleted or overwritten by this path.
                DeleteRegistryKeyTree(sourcePath);
                using var sourceVerification = OpenRegistryKey(sourcePath, writable: false);
                if (sourceVerification is not null)
                {
                    throw new InvalidOperationException(
                        $"The redundant source shell-extension registration still exists after reconciliation: {sourcePath}.");
                }

                using var destinationVerification = OpenRegistryKey(destinationPath, writable: false);
                if (destinationVerification is null
                    || !string.Equals(
                        destinationVerification.GetValue(null)?.ToString(),
                        destinationDefaultValue,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The existing destination shell-extension registration was lost or changed during reconciliation: {destinationPath}.");
                }

                return;
            }

            throw new InvalidOperationException(
                $"Cannot move shell-extension registration because the destination already exists and is not equivalent to the source. Source={sourcePath}; Destination={destinationPath}. Resolve the active/disabled registration conflict explicitly.");
        }

        RegistryKey? destination = null;
        try
        {
            using (var source = OpenRegistryKey(sourcePath, writable: false)
                   ?? throw new InvalidOperationException($"Source shell-extension registration was not found: {sourcePath}."))
            {
                destination = CreateRegistrySubKey(destinationPath, writable: true)
                    ?? throw new InvalidOperationException($"Unable to create destination shell-extension registration: {destinationPath}.");
                CopyRegistryKeyTree(source, destination);
                VerifyRegistryKeyTree(source, destination);
                destination.Dispose();
                destination = null;
            }

            DeleteRegistryKeyTree(sourcePath);
            using var sourceVerification = OpenRegistryKey(sourcePath, writable: false);
            if (sourceVerification is not null)
            {
                throw new InvalidOperationException($"The source shell-extension registration still exists after move: {sourcePath}.");
            }
        }
        catch
        {
            destination?.Dispose();
            // Only remove a destination which was created during this failed move.
            // An already-existing destination is rejected before this point.
            using var sourceStillExists = OpenRegistryKey(sourcePath, writable: false);
            if (sourceStillExists is not null)
            {
                DeleteRegistryKeyTree(destinationPath);
            }

            throw;
        }
    }

    private static void CopyRegistryKeyTree(RegistryKey source, RegistryKey destination)
    {
        foreach (var valueName in source.GetValueNames())
        {
            // Registry values cannot retain a null payload; use the Windows-compatible
            // empty string representation if a provider exposes one as null.
            destination.SetValue(valueName, source.GetValue(valueName) ?? string.Empty, source.GetValueKind(valueName));
        }

        foreach (var subKeyName in source.GetSubKeyNames())
        {
            using var sourceChild = source.OpenSubKey(subKeyName, writable: false)
                ?? throw new InvalidOperationException($"Unable to read source subkey '{subKeyName}'.");
            using var destinationChild = destination.CreateSubKey(subKeyName, writable: true)
                ?? throw new InvalidOperationException($"Unable to create destination subkey '{subKeyName}'.");
            CopyRegistryKeyTree(sourceChild, destinationChild);
        }
    }

    private static void VerifyRegistryKeyTree(RegistryKey source, RegistryKey destination)
    {
        var sourceValues = source.GetValueNames().OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var destinationValues = destination.GetValueNames().OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!sourceValues.SequenceEqual(destinationValues, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The destination shell-extension registration values do not match the source.");
        }

        foreach (var valueName in sourceValues)
        {
            if (source.GetValueKind(valueName) != destination.GetValueKind(valueName)
                || !RegistryValueEquals(source.GetValue(valueName), destination.GetValue(valueName)))
            {
                throw new InvalidOperationException($"The destination value '{valueName}' does not match the source registration.");
            }
        }

        var sourceSubKeys = source.GetSubKeyNames().OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        var destinationSubKeys = destination.GetSubKeyNames().OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!sourceSubKeys.SequenceEqual(destinationSubKeys, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The destination shell-extension registration subkeys do not match the source.");
        }

        foreach (var subKeyName in sourceSubKeys)
        {
            using var sourceChild = source.OpenSubKey(subKeyName, writable: false)
                ?? throw new InvalidOperationException($"Unable to re-open source subkey '{subKeyName}'.");
            using var destinationChild = destination.OpenSubKey(subKeyName, writable: false)
                ?? throw new InvalidOperationException($"Unable to re-open destination subkey '{subKeyName}'.");
            VerifyRegistryKeyTree(sourceChild, destinationChild);
        }
    }

    /// <summary>
    /// Determines whether two registry key trees represent the same registration.
    /// The comparison is conservative: every value (including the default value
    /// which carries the Handler CLSID), its <see cref="RegistryValueKind"/>, its
    /// payload (strings, binary and multi-string values), and every nested subkey
    /// must match. Any difference makes the registrations non-equivalent so the
    /// caller can treat the situation as a conflict instead of destroying data.
    /// Security descriptors are intentionally not compared: the disabled mirror
    /// may legitimately inherit different container ACLs than the recreated copy.
    /// </summary>
    private static bool RegistryKeyTreesEquivalent(RegistryKey left, RegistryKey right)
    {
        var leftValues = left.GetValueNames().OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var rightValues = right.GetValueNames().OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!leftValues.SequenceEqual(rightValues, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var valueName in leftValues)
        {
            if (left.GetValueKind(valueName) != right.GetValueKind(valueName)
                || !RegistryValueEquals(left.GetValue(valueName), right.GetValue(valueName)))
            {
                return false;
            }
        }

        var leftSubKeys = left.GetSubKeyNames().OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        var rightSubKeys = right.GetSubKeyNames().OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!leftSubKeys.SequenceEqual(rightSubKeys, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var subKeyName in leftSubKeys)
        {
            using var leftChild = left.OpenSubKey(subKeyName, writable: false);
            using var rightChild = right.OpenSubKey(subKeyName, writable: false);
            if (leftChild is null || rightChild is null || !RegistryKeyTreesEquivalent(leftChild, rightChild))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RegistryValueEquals(object? left, object? right)
    {
        if (left is string[] leftArray && right is string[] rightArray)
        {
            return leftArray.SequenceEqual(rightArray, StringComparer.Ordinal);
        }

        if (left is byte[] leftBytes && right is byte[] rightBytes)
        {
            return leftBytes.SequenceEqual(rightBytes);
        }

        return Equals(left, right);
    }

    private static void DeleteRegistryKey(string registryPath)
    {
        DeleteRegistryKeyTree(registryPath);
    }

    private static EnhanceAttributeWriteResult? SetEnhanceShellItemEnabled(
        string relativeGroupPath,
        XElement itemElement,
        bool enable,
        string cultureName,
        BackendUserContext userContext,
        FileLogger? logger)
    {
        var keyName = itemElement.Attribute("KeyName")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(keyName))
        {
            throw new InvalidOperationException("Enhance shell items require a KeyName attribute.");
        }

        var registryPath = $@"{relativeGroupPath}\shell\{keyName}";
        if (enable)
        {
            return WriteEnhanceSubKeysValue(itemElement, registryPath, cultureName, userContext, logger);
        }
        else
        {
            DeleteUserClassesSubKeyTree(userContext, registryPath);

            // Best-effort legacy machine-wide cleanup for built-in enhance items.
            try
            {
                DeleteRegistrySubKeyTreeWithFallback(registryPath);
            }
            catch (UnauthorizedAccessException)
            {
                // Log a warning but do not fail the user-level operation.
                _ = registryPath; // Suppress unused warning; path is logged by caller.
            }

            return null;
        }
    }

    private static void SetEnhanceShellExItemEnabled(
        string relativeGroupPath,
        XElement itemElement,
        bool enable,
        BackendUserContext userContext)
    {
        var guidText = itemElement.Element("Guid")?.Value?.Trim();
        if (!Guid.TryParse(guidText, out var guid))
        {
            throw new InvalidOperationException("Enhance shell extension items require a valid Guid element.");
        }

        var keyName = itemElement.Element("KeyName")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(keyName))
        {
            keyName = guid.ToString("B");
        }

        var handlersRelativePath = $@"{relativeGroupPath}\shellex\ContextMenuHandlers";
        if (enable)
        {
            EnableBackupPrivilege();

            using var handlersKey = CreateUserClassesSubKey(userContext, handlersRelativePath);

            foreach (var subKeyName in handlersKey.GetSubKeyNames())
            {
                using var subKey = handlersKey.OpenSubKey(subKeyName, writable: false);
                var value = subKey?.GetValue(null)?.ToString();
                if (Guid.TryParse(value, out var actualGuid) && actualGuid == guid)
                {
                    return;
                }
            }

            var targetRelativePath = $@"{handlersRelativePath}\{keyName}";
            using var targetKey = OpenUserClassesSubKey(userContext, targetRelativePath, writable: false);
            var targetValue = targetKey?.GetValue(null)?.ToString();
            if (targetKey is not null
                && (!Guid.TryParse(targetValue, out var existingGuid) || existingGuid != guid))
            {
                targetRelativePath = GetUniqueUserClassesPath(userContext, handlersRelativePath, keyName);
            }

            using var targetKeyWritable = CreateUserClassesSubKey(userContext, targetRelativePath);
            targetKeyWritable.SetValue(string.Empty, guid.ToString("B"), RegistryValueKind.String);
        }
        else
        {
            EnableBackupPrivilege();

            using var handlersKey = OpenUserClassesSubKey(userContext, handlersRelativePath, writable: true);
            if (handlersKey is null)
            {
                return;
            }

            foreach (var subKeyName in handlersKey.GetSubKeyNames())
            {
                using var subKey = handlersKey.OpenSubKey(subKeyName, writable: false);
                var value = subKey?.GetValue(null)?.ToString();
                if (Guid.TryParse(value, out var actualGuid) && actualGuid == guid)
                {
                    try
                    {
                        handlersKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        DeleteUserClassesSubKeyTree(userContext, $@"{handlersRelativePath}\{subKeyName}");
                    }
                }
            }

            // Best-effort legacy machine-wide cleanup.
            try
            {
                using var machineHandlersKey = Registry.ClassesRoot.OpenSubKey(handlersRelativePath, writable: true);
                if (machineHandlersKey is not null)
                {
                    foreach (var subKeyName in machineHandlersKey.GetSubKeyNames())
                    {
                        using var subKey = machineHandlersKey.OpenSubKey(subKeyName, writable: false);
                        var value = subKey?.GetValue(null)?.ToString();
                        if (Guid.TryParse(value, out var actualGuid) && actualGuid == guid)
                        {
                            try
                            {
                                machineHandlersKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                            }
                            catch (UnauthorizedAccessException)
                            {
                                DeleteRegistrySubKeyTreeWithFallback($@"{handlersRelativePath}\{subKeyName}");
                            }
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Legacy machine cleanup is best-effort; do not fail the user-level operation.
            }
        }
    }

    private static string GetUniqueUserClassesPath(BackendUserContext userContext, string parentRelativePath, string baseKeyName)
    {
        var candidate = baseKeyName;
        var index = 1;
        while (OpenUserClassesSubKey(userContext, $@"{parentRelativePath}\{candidate}", writable: false) is not null)
        {
            candidate = $"{baseKeyName} ({index})";
            index++;
        }

        return $@"{parentRelativePath}\{candidate}";
    }

    private static EnhanceAttributeWriteResult? WriteEnhanceSubKeysValue(
        XElement keyElement,
        string registryPath,
        string cultureName,
        BackendUserContext userContext,
        FileLogger? logger)
    {
        if (!ShouldIncludeNode(keyElement, cultureName))
        {
            return null;
        }

        EnableBackupPrivilege();
        EnhanceAttributeWriteResult? result = null;

        var defaultValue = keyElement.Attribute("Default")?.Value;
        if (!string.IsNullOrWhiteSpace(defaultValue))
        {
            if (string.Equals(keyElement.Name.LocalName, "Command", StringComparison.OrdinalIgnoreCase))
            {
                defaultValue = CanonicalizeEnhanceCommandDefaultValue(defaultValue);
            }
            else
            {
                defaultValue = EnhanceMenuTextSanitizer.StripMenuAcceleratorAmpersands(defaultValue);
            }

            using var userClasses = GetUserClassesRoot(userContext, writable: true);
            using var key = userClasses.CreateSubKey(registryPath, writable: true);
            var expandedDefaultValue = string.Equals(keyElement.Name.LocalName, "Command", StringComparison.OrdinalIgnoreCase)
                ? ExpandEnhanceCommandEnvironmentVariables(defaultValue)
                : Environment.ExpandEnvironmentVariables(defaultValue);
            key?.SetValue(string.Empty, expandedDefaultValue, RegistryValueKind.String);
        }
        else if (string.Equals(keyElement.Name.LocalName, "Command", StringComparison.OrdinalIgnoreCase))
        {
            WriteEnhanceCommandValue(keyElement, registryPath, cultureName, userContext, logger);
        }

        result = WriteEnhanceAttributesValue(keyElement.Element("Value"), registryPath, cultureName, userContext);

        var subKeyElement = keyElement.Element("SubKey");
        if (subKeyElement is null)
        {
            return result;
        }

        foreach (var childElement in subKeyElement.Elements())
        {
            var childResult = WriteEnhanceSubKeysValue(childElement, $@"{registryPath}\{childElement.Name.LocalName}", cultureName, userContext, logger);
            result ??= childResult;
        }

        return result;
    }

    private static EnhanceAttributeWriteResult? WriteEnhanceAttributesValue(XElement? valueElement, string registryPath, string cultureName, BackendUserContext userContext)
    {
        if (valueElement is null || !ShouldIncludeNode(valueElement, cultureName))
        {
            return null;
        }

        EnableBackupPrivilege();

        using var userClasses = GetUserClassesRoot(userContext, writable: true);
        using var key = userClasses.CreateSubKey(registryPath, writable: true);
        if (key is null)
        {
            throw new InvalidOperationException(
                $"Unable to create per-user registry key: HKEY_USERS\\{userContext.Sid}\\{UserClassesPath}\\{registryPath}.");
        }

        string? selectedMuiVerb = null;
        var cultureOverrideApplied = false;
        foreach (var valueNode in SelectLocalizedElementsForWrite(valueElement.Elements(), cultureName))
        {
            var nodeHasExactCulture = HasExactNormalizedCulture(valueNode, cultureName);
            foreach (var attribute in valueNode.Attributes())
            {
                if (string.IsNullOrWhiteSpace(attribute.Name.LocalName)
                    || string.Equals(attribute.Name.LocalName, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var attributeValue = attribute.Value;
                switch (valueNode.Name.LocalName)
                {
                    case "REG_SZ":
                        if (string.Equals(attribute.Name.LocalName, "MUIVerb", StringComparison.OrdinalIgnoreCase))
                        {
                            attributeValue = EnhanceMenuTextSanitizer.StripMenuAcceleratorAmpersands(attributeValue);
                            selectedMuiVerb = Environment.ExpandEnvironmentVariables(attributeValue);
                            cultureOverrideApplied = nodeHasExactCulture;
                        }

                        key.SetValue(attribute.Name.LocalName, Environment.ExpandEnvironmentVariables(attributeValue), RegistryValueKind.String);
                        break;
                    case "REG_EXPAND_SZ":
                        key.SetValue(attribute.Name.LocalName, attributeValue, RegistryValueKind.ExpandString);
                        break;
                    case "REG_BINARY":
                        key.SetValue(attribute.Name.LocalName, ConvertToBinary(attributeValue), RegistryValueKind.Binary);
                        break;
                    case "REG_DWORD":
                        var numericBase = attributeValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10;
                        var numericValue = numericBase == 16 ? attributeValue[2..] : attributeValue;
                        key.SetValue(attribute.Name.LocalName, Convert.ToInt32(numericValue, numericBase), RegistryValueKind.DWord);
                        break;
                }
            }
        }

        return selectedMuiVerb is null
            ? null
            : new EnhanceAttributeWriteResult(selectedMuiVerb, cultureOverrideApplied);
    }

    private static void WriteEnhanceCommandValue(
        XElement commandElement,
        string registryPath,
        string cultureName,
        BackendUserContext userContext,
        FileLogger? logger)
    {
        var compilation = CompileEnhanceCommandValue(commandElement, cultureName);

        logger?.LogFireAndForget(
            "EnhanceCommandCompile: "
            + $"KeyName={GetEnhanceCommandKeyName(commandElement)}, "
            + $"RegistryPath={registryPath}, "
            + $"FileName={compilation.FileName}, "
            + $"Verb={compilation.ShellExecuteVerb}, "
            + $"GeneratedCommandKind={compilation.GeneratedCommandKind}, "
            + $"GeneratedFileCreated={compilation.GeneratedFileCreated}, "
            + $"WrapperReason={compilation.WrapperReason}, "
            + $"FinalCommand={compilation.Command}.");

        EnableBackupPrivilege();

        using var userClasses = GetUserClassesRoot(userContext, writable: true);
        using var key = userClasses.CreateSubKey(registryPath, writable: true);
        key?.SetValue(string.Empty, compilation.Command, RegistryValueKind.String);
    }

    private static EnhanceCommandCompilationResult CompileEnhanceCommandValue(XElement commandElement, string cultureName)
    {
        var fileNameElement = commandElement.Element("FileName");
        var argumentsElement = commandElement.Element("Arguments");
        var shellExecuteElement = commandElement.Element("ShellExecute");
        var powerShellScriptElement = SelectLocalizedElementForWrite(commandElement.Elements("PowerShellScript"), cultureName);

        if (powerShellScriptElement is not null)
        {
            var script = GetDirectElementText(powerShellScriptElement).Trim();
            var runtimeArgument = powerShellScriptElement.Attribute("Argument")?.Value?.Trim();
            var elevatedCommand = BuildElevatedPowerShellCommand(script, runtimeArgument);
            return new EnhanceCommandCompilationResult(
                elevatedCommand,
                "powershell.exe",
                shellExecuteElement is not null,
                shellExecuteElement?.Attribute("Verb")?.Value?.Trim() ?? string.Empty,
                "ElevatedPowerShellBlock",
                false,
                string.Empty);
        }

        var fileName = fileNameElement?.Value?.Trim();
        var arguments = argumentsElement?.Value?.Trim();
        var generatedFileCreated = false;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = CreateEnhanceCommandFile(fileNameElement, cultureName);
            generatedFileCreated = !string.IsNullOrWhiteSpace(fileName);
        }

        if (string.IsNullOrWhiteSpace(arguments))
        {
            arguments = CreateEnhanceCommandFile(argumentsElement, cultureName);
            generatedFileCreated = generatedFileCreated || !string.IsNullOrWhiteSpace(arguments);
        }

        fileName = CanonicalizeEnhanceExecutableFileName(fileName);
        var rawFileName = fileName ?? string.Empty;
        var rawArguments = arguments ?? string.Empty;

        string command;
        var wrapperReason = string.Empty;
        var generatedCommandKind = "Direct";
        var shellExecuteVerb = shellExecuteElement?.Attribute("Verb")?.Value?.Trim() ?? string.Empty;
        if (shellExecuteElement is not null)
        {
            var verb = shellExecuteElement.Attribute("Verb")?.Value ?? "open";
            var windowStyle = int.TryParse(shellExecuteElement.Attribute("WindowStyle")?.Value, out var parsedStyle) ? parsedStyle : 1;
            var directory = shellExecuteElement.Attribute("Directory") is { } directoryAttribute
                ? Environment.ExpandEnvironmentVariables(directoryAttribute.Value)
                : string.Empty;
            if (string.Equals(verb, "runas", StringComparison.OrdinalIgnoreCase))
            {
                command = BuildPowerShellRunAsCommand(
                    rawFileName,
                    $"{argumentsElement?.Attribute("Prefix")?.Value}{rawArguments}{argumentsElement?.Attribute("Suffix")?.Value}");
                generatedCommandKind = "PowerShellRunAs";
            }
            else if (!RequiresShellExecuteWrapper(shellExecuteElement))
            {
                fileName = ExpandEnhanceCommandEnvironmentVariables(rawFileName);
                arguments = ExpandEnhanceCommandEnvironmentVariables(rawArguments);
                arguments = CanonicalizeEnhanceCommandArguments(arguments);
                arguments = $"{argumentsElement?.Attribute("Prefix")?.Value}{arguments}{argumentsElement?.Attribute("Suffix")?.Value}";
                command = BuildDirectEnhanceCommand(fileName, arguments);
            }
            else
            {
                wrapperReason = GetShellExecuteWrapperReason(shellExecuteElement);
                fileName = ExpandEnhanceCommandEnvironmentVariables(rawFileName);
                arguments = ExpandEnhanceCommandEnvironmentVariables(rawArguments);
                arguments = CanonicalizeEnhanceCommandArguments(arguments);
                arguments = $"{argumentsElement?.Attribute("Prefix")?.Value}{arguments}{argumentsElement?.Attribute("Suffix")?.Value}";
                command = BuildShellExecuteCommand(fileName, arguments, verb, windowStyle, directory);
                generatedCommandKind = "LegacyMshta";
            }
        }
        else
        {
            fileName = ExpandEnhanceCommandEnvironmentVariables(rawFileName);
            arguments = ExpandEnhanceCommandEnvironmentVariables(rawArguments);
            arguments = CanonicalizeEnhanceCommandArguments(arguments);
            arguments = $"{argumentsElement?.Attribute("Prefix")?.Value}{arguments}{argumentsElement?.Attribute("Suffix")?.Value}";
            command = BuildDirectEnhanceCommand(fileName, arguments);
        }

        return new EnhanceCommandCompilationResult(
            command,
            fileName ?? string.Empty,
            shellExecuteElement is not null,
            shellExecuteVerb,
            generatedCommandKind,
            generatedFileCreated,
            wrapperReason);
    }

    internal static int ValidateEnhanceMenuDictionary(string dictionaryPath, string? cultureName, TextWriter writer)
    {
        var normalizedCulture = NormalizeEnhanceCultureName(cultureName);
        var document = XDocument.Load(dictionaryPath, LoadOptions.PreserveWhitespace);
        var commandElements = document.Descendants("Command").ToList();
        var flaggedCount = 0;

        writer.WriteLine($"EnhanceMenus validation: Path={dictionaryPath}, Culture={normalizedCulture}, Commands={commandElements.Count}");
        writer.WriteLine("ItemKey\tRegistryPath\tCommandKind\tFlags");

        foreach (var commandElement in commandElements)
        {
            if (!ShouldIncludeNode(commandElement, normalizedCulture))
            {
                continue;
            }

            var itemKey = GetEnhanceCommandKeyName(commandElement);
            var registryPath = GetEnhanceDiagnosticRegistryPath(commandElement);
            var (commandKind, command) = CompileEnhanceDiagnosticCommand(commandElement, normalizedCulture);
            var flags = GetEnhanceCommandLegacyFlags(command);

            if (flags.Count > 0)
            {
                flaggedCount++;
            }

            writer.WriteLine($"{itemKey}\t{registryPath}\t{commandKind}\t{(flags.Count == 0 ? "OK" : string.Join(", ", flags))}");
        }

        writer.WriteLine(flaggedCount == 0
            ? "EnhanceMenus validation passed: no flagged legacy command patterns."
            : $"EnhanceMenus validation failed: {flaggedCount} command(s) contain flagged legacy patterns.");

        return flaggedCount == 0 ? 0 : 2;
    }

    internal static int ValidateEnhanceLocalizationSelection(TextWriter writer)
    {
        var failures = new List<string>();
        var valueNodes = ParseElements(
            """
            <Root>
              <REG_SZ MUIVerb="系统信息" />
              <REG_SZ MUIVerb="System Info"><Culture>en-US</Culture></REG_SZ>
              <REG_SZ MUIVerb="系統資訊"><Culture>zh-TW</Culture></REG_SZ>
            </Root>
            """);
        var scriptNodes = ParseElements(
            """
            <Root>
              <PowerShellScript>simplified-script</PowerShellScript>
              <PowerShellScript>traditional-script<Culture>zh-TW</Culture></PowerShellScript>
              <PowerShellScript>english-script<Culture>en-US</Culture></PowerShellScript>
            </Root>
            """);

        ExpectEqual("zh-CN value selection", "系统信息", GetFinalSelectedMuiVerb(valueNodes, "zh-CN"), failures);
        ExpectEqual("zh-TW value selection", "系統資訊", GetFinalSelectedMuiVerb(valueNodes, "zh-TW"), failures);
        ExpectEqual("en-US value selection", "System Info", GetFinalSelectedMuiVerb(valueNodes, "en-US"), failures);
        ExpectFalse(
            "zh-CN excludes zh-TW value node",
            SelectLocalizedElementsForWrite(valueNodes, "zh-CN").Any(element => HasExactNormalizedCulture(element, "zh-TW")),
            failures);
        ExpectEqual("zh-TW normalization", "zh-TW", NormalizeEnhanceCultureName("zh-TW"), failures);
        ExpectEqual(
            "PowerShellScript zh-CN selection",
            "simplified-script",
            GetDirectElementText(SelectLocalizedElementForWrite(scriptNodes, "zh-CN")!).Trim(),
            failures);
        ExpectEqual(
            "PowerShellScript en-US selection",
            "english-script",
            GetDirectElementText(SelectLocalizedElementForWrite(scriptNodes, "en-US")!).Trim(),
            failures);

        if (failures.Count == 0)
        {
            writer.WriteLine("Enhance localization selection validation passed.");
            return 0;
        }

        writer.WriteLine("Enhance localization selection validation failed:");
        foreach (var failure in failures)
        {
            writer.WriteLine($"- {failure}");
        }

        return 1;

        static IReadOnlyList<XElement> ParseElements(string xml)
            => XElement.Parse(xml).Elements().ToList();

        static string? GetFinalSelectedMuiVerb(IReadOnlyList<XElement> elements, string cultureName)
            => SelectLocalizedElementsForWrite(elements, cultureName)
                .Select(element => element.Attribute("MUIVerb")?.Value)
                .LastOrDefault(value => !string.IsNullOrWhiteSpace(value));

        static void ExpectEqual(string name, string expected, string? actual, List<string> failures)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                failures.Add($"{name}: expected '{expected}', got '{actual ?? "<null>"}'.");
            }
        }

        static void ExpectFalse(string name, bool actual, List<string> failures)
        {
            if (actual)
            {
                failures.Add($"{name}: expected false, got true.");
            }
        }
    }

    private static (string CommandKind, string Command) CompileEnhanceDiagnosticCommand(XElement commandElement, string cultureName)
    {
        var defaultValue = commandElement.Attribute("Default")?.Value;
        if (!string.IsNullOrWhiteSpace(defaultValue))
        {
            var command = ExpandEnhanceCommandEnvironmentVariables(CanonicalizeEnhanceCommandDefaultValue(defaultValue));
            return ("Default", command);
        }

        if (commandElement.Element("Value") is not null
            && commandElement.Element("PowerShellScript") is null
            && commandElement.Element("FileName") is null
            && commandElement.Element("Arguments") is null)
        {
            return ("RegistryValuesOnly", string.Empty);
        }

        var compilation = CompileEnhanceCommandValue(commandElement, cultureName);
        return (compilation.GeneratedCommandKind, compilation.Command);
    }

    private static string GetEnhanceDiagnosticRegistryPath(XElement commandElement)
    {
        var groupElement = commandElement.Ancestors("Group").FirstOrDefault();
        var rootPath = groupElement?.Element("RegPath")?.Value?.Trim();
        var keyParts = commandElement
            .Ancestors()
            .TakeWhile(element => !string.Equals(element.Name.LocalName, "Group", StringComparison.OrdinalIgnoreCase))
            .Where(element => element.Attribute("KeyName") is not null)
            .Reverse()
            .Select(element => element.Attribute("KeyName")!.Value.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        keyParts.Add("command");
        return string.IsNullOrWhiteSpace(rootPath)
            ? string.Join(@"\", keyParts)
            : rootPath + @"\shell\" + string.Join(@"\", keyParts);
    }

    private static IReadOnlyList<string> GetEnhanceCommandLegacyFlags(string command)
    {
        var flags = new List<string>();
        AddFlagIfMatch(flags, command, @"(?i)\bmshta\b", "mshta");
        AddFlagIfMatch(flags, command, @"(?i)\bvbscript:", "vbscript:");
        AddFlagIfMatch(flags, command, @"(?i)\bWscript\.exe\b", "Wscript.exe");
        AddFlagIfMatch(flags, command, @"(?i)\.vbs\b", ".vbs");
        AddFlagIfMatch(flags, command, @"(?i)^\s*""?cmd(?:\.exe)?""?(?=\s|$)", "bare cmd");
        AddFlagIfMatch(flags, command, @"(?i)^\s*""?explorer(?:\.exe)?""?(?=\s|$)", "bare explorer");
        AddFlagIfMatch(flags, command, @"(?i)ContextMenuMgr", "ContextMenuMgr");
        AddFlagIfMatch(flags, command, @"(?i)\bBackend\b", "Backend");
        AddFlagIfMatch(flags, command, @"(?i)\bTrayHost\b", "TrayHost");
        AddFlagIfMatch(flags, command, @"(?i)\bNamedPipe\b", "NamedPipe");
        AddFlagIfMatch(flags, command, @"(?i)\bpipe\b", "pipe");
        return flags;
    }

    private static void AddFlagIfMatch(List<string> flags, string command, string pattern, string flag)
    {
        if (Regex.IsMatch(command, pattern))
        {
            flags.Add(flag);
        }
    }

    private sealed record EnhanceCommandCompilationResult(
        string Command,
        string FileName,
        bool HasShellExecute,
        string ShellExecuteVerb,
        string GeneratedCommandKind,
        bool GeneratedFileCreated,
        string WrapperReason);

    private sealed record EnhanceAttributeWriteResult(string MuiVerb, bool CultureOverrideApplied);

    private static bool RequiresShellExecuteWrapper(XElement shellExecuteElement)
        => !string.IsNullOrEmpty(GetShellExecuteWrapperReason(shellExecuteElement));

    private static string GetShellExecuteWrapperReason(XElement shellExecuteElement)
    {
        var verb = shellExecuteElement.Attribute("Verb")?.Value?.Trim();
        if (!string.IsNullOrEmpty(verb)
            && !string.Equals(verb, "open", StringComparison.OrdinalIgnoreCase))
        {
            // TODO: runas/admin enhance items need a separate modern launcher strategy.
            return $"Verb={verb}";
        }

        var directory = shellExecuteElement.Attribute("Directory")?.Value?.Trim();
        if (!string.IsNullOrEmpty(directory))
        {
            return "Directory";
        }

        foreach (var attribute in shellExecuteElement.Attributes())
        {
            var name = attribute.Name.LocalName;
            if (string.Equals(name, "Verb", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "WindowStyle", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Directory", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(attribute.Value))
            {
                return $"Attribute={name}";
            }
        }

        return string.Empty;
    }

    private static string BuildDirectEnhanceCommand(string fileName, string arguments)
    {
        var command = QuoteEnhanceExecutablePath(fileName);
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            command += $" {arguments}";
        }

        return command;
    }

    private static string QuoteEnhanceExecutablePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var trimmed = fileName.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            return trimmed;
        }

        if (trimmed.Contains(' ')
            && (Path.IsPathRooted(trimmed) || Regex.IsMatch(trimmed, @"^%[^%]+%[\\/]", RegexOptions.IgnoreCase)))
        {
            return $"\"{trimmed}\"";
        }

        return trimmed;
    }

    private static string GetEnhanceCommandKeyName(XElement commandElement)
    {
        foreach (var element in commandElement.AncestorsAndSelf())
        {
            var keyName = element.Attribute("KeyName")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(keyName))
            {
                return keyName;
            }
        }

        return "<unknown>";
    }

    private static string CanonicalizeEnhanceExecutableFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var trimmed = fileName.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        return trimmed.Equals("cmd", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            ? @"C:\Windows\System32\cmd.exe"
            : trimmed.Equals("explorer", StringComparison.OrdinalIgnoreCase)
              || trimmed.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                ? @"C:\Windows\explorer.exe"
                : trimmed;
    }

    private static string CanonicalizeEnhanceCommandDefaultValue(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        var leadingWhitespaceLength = command.Length - command.TrimStart().Length;
        var leadingWhitespace = command[..leadingWhitespaceLength];
        var trimmedStart = command[leadingWhitespaceLength..];

        foreach (var (prefix, replacement) in new[]
                 {
                     ("cmd.exe ", @"C:\Windows\System32\cmd.exe "),
                     ("cmd ", @"C:\Windows\System32\cmd.exe "),
                     ("explorer.exe ", @"C:\Windows\explorer.exe "),
                     ("explorer ", @"C:\Windows\explorer.exe ")
                 })
        {
            if (trimmedStart.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = trimmedStart[prefix.Length..];
                return leadingWhitespace + replacement + rest;
            }
        }

        return command;
    }

    private static string CanonicalizeEnhanceCommandArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return arguments;
        }

        return Regex.Replace(
            arguments,
            @"(?i)(^|[&|]\s*)start\s+explorer(?:\.exe)?(?=\s|$)",
            match => $@"{match.Groups[1].Value}start C:\Windows\\explorer.exe");
    }

    private static string ExpandEnhanceCommandEnvironmentVariables(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var protectedTokens = new Dictionary<string, string>(StringComparer.Ordinal);
        var protectedValue = Regex.Replace(value, @"C:\Windows", AddProtectedToken, RegexOptions.IgnoreCase);
        protectedValue = Regex.Replace(
            protectedValue,
            @"%(TEMP|TMP|LOCALAPPDATA|APPDATA|USERPROFILE)%",
            AddProtectedToken,
            RegexOptions.IgnoreCase);

        var expanded = Environment.ExpandEnvironmentVariables(protectedValue);
        foreach (var (token, original) in protectedTokens)
        {
            expanded = expanded.Replace(token, original, StringComparison.Ordinal);
        }

        return expanded;

        string AddProtectedToken(Match match)
        {
            var token = $"\uF001{protectedTokens.Count}\uF001";
            protectedTokens[token] = match.Value;
            return token;
        }
    }

    private static string CreateEnhanceCommandFile(XElement? parentElement, string cultureName)
    {
        if (parentElement is null)
        {
            return string.Empty;
        }

        var generatedDir = RuntimePaths.GeneratedProgramsDirectory;
        Directory.CreateDirectory(generatedDir);

        var path = string.Empty;
        var createFileElement = SelectLocalizedElementForWrite(parentElement.Elements("CreateFile"), cultureName);
        if (createFileElement is not null)
        {
            var fileName = createFileElement.Attribute("FileName")?.Value;
            var content = createFileElement.Attribute("Content")?.Value ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var safeFileName = SanitizeEnhanceProgramFileName(fileName);
                var filePath = Path.Combine(generatedDir, safeFileName);
                var encoding = string.Equals(Path.GetExtension(fileName), ".bat", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetExtension(fileName), ".cmd", StringComparison.OrdinalIgnoreCase)
                        ? Encoding.Default
                        : Encoding.Unicode;

                path = filePath;

                File.Delete(filePath);
                File.WriteAllText(filePath, content, encoding);
            }
        }

        return path;
    }

    private static string GetDirectElementText(XElement element)
        => string.Concat(element.Nodes().OfType<XText>().Select(node => node.Value));

    private static string BuildPowerShellRunAsCommand(string fileName, string arguments)
    {
        var runtimeArguments = GetRuntimePlaceholderArguments(arguments);
        var script = "& { "
                     + BuildPowerShellParamList(runtimeArguments)
                     + $"Start-Process -Verb RunAs -FilePath {QuotePowerShellSingleQuotedString(fileName)}";
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            script += $" -ArgumentList ({BuildPowerShellStringExpression(arguments, runtimeArguments)})";
        }

        script += " }";
        return BuildPowerShellCommand(script, runtimeArguments);
    }

    private static string BuildElevatedPowerShellCommand(string script, string? runtimeArgument)
    {
        var runtimeArguments = string.IsNullOrWhiteSpace(runtimeArgument)
            ? []
            : new[] { runtimeArgument };

        var innerScript = runtimeArguments.Length > 0
            ? $"& {{ param($p); {script} }}"
            : $"& {{ {script} }}";
        var innerScriptExpression = BuildPowerShellStringExpression(innerScript, []);
        var innerCommandLineExpression = string.Join(
            " + ",
            QuotePowerShellSingleQuotedString("-NoProfile -ExecutionPolicy Bypass -Command "),
            "[char]34",
            innerScriptExpression,
            "[char]34");

        if (runtimeArguments.Length > 0)
        {
            innerCommandLineExpression += " + ' ' + [char]34 + $p0 + [char]34";
        }

        var outerScript = "& { "
                          + BuildPowerShellParamList(runtimeArguments)
                          + "Start-Process powershell.exe -Verb RunAs -ArgumentList ("
                          + innerCommandLineExpression
                          + ") }";
        return BuildPowerShellCommand(outerScript, runtimeArguments);
    }

    private static string BuildPowerShellCommand(string script, IReadOnlyList<string> runtimeArguments)
    {
        var command = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"{script}\"";
        foreach (var argument in runtimeArguments)
        {
            command += $" \"{argument}\"";
        }

        return command;
    }

    private static string BuildPowerShellParamList(IReadOnlyList<string> runtimeArguments)
    {
        if (runtimeArguments.Count == 0)
        {
            return string.Empty;
        }

        return "param("
               + string.Join(",", Enumerable.Range(0, runtimeArguments.Count).Select(index => $"$p{index}"))
               + ");";
    }

    private static string BuildPowerShellStringExpression(string value, IReadOnlyList<string> runtimeArguments)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        var parts = new List<string>();
        var index = 0;
        while (index < value.Length)
        {
            var placeholderIndex = -1;
            var placeholderValue = string.Empty;
            var placeholderVariable = string.Empty;
            for (var i = 0; i < runtimeArguments.Count; i++)
            {
                var candidate = runtimeArguments[i];
                var candidateIndex = value.IndexOf(candidate, index, StringComparison.OrdinalIgnoreCase);
                if (candidateIndex >= 0 && (placeholderIndex < 0 || candidateIndex < placeholderIndex))
                {
                    placeholderIndex = candidateIndex;
                    placeholderValue = candidate;
                    placeholderVariable = $"$p{i}";
                }
            }

            var nextLiteralEnd = placeholderIndex >= 0 ? placeholderIndex : value.Length;
            AddPowerShellLiteralExpressionParts(parts, value[index..nextLiteralEnd]);
            if (placeholderIndex < 0)
            {
                break;
            }

            parts.Add(placeholderVariable);
            index = placeholderIndex + placeholderValue.Length;
        }

        return parts.Count == 0 ? "''" : string.Join(" + ", parts);
    }

    private static void AddPowerShellLiteralExpressionParts(List<string> parts, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '"')
            {
                continue;
            }

            if (i > start)
            {
                parts.Add(QuotePowerShellSingleQuotedString(value[start..i]));
            }

            parts.Add("[char]34");
            start = i + 1;
        }

        if (start < value.Length)
        {
            parts.Add(QuotePowerShellSingleQuotedString(value[start..]));
        }
    }

    private static string QuotePowerShellSingleQuotedString(string value)
        => $"'{(value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal)}'";

    private static string[] GetRuntimePlaceholderArguments(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        var result = new List<string>();
        foreach (var placeholder in new[] { "%1", "%v" })
        {
            if (value.Contains(placeholder, StringComparison.OrdinalIgnoreCase)
                && !result.Contains(placeholder, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(placeholder);
            }
        }

        return result.ToArray();
    }

    private static string BuildShellExecuteCommand(
        string fileName,
        string arguments,
        string verb,
        int windowStyle,
        string? directory)
    {
        arguments = arguments.Replace("\"", "\"\"");
        directory = directory is null
            ? Path.GetDirectoryName(ExtractExecutablePath(fileName))
            : directory;

        return "mshta vbscript:createobject(\"shell.application\").shellexecute"
            + $"(\"{fileName}\",\"{arguments}\",\"{directory}\",\"{verb}\",{windowStyle})(close)";
    }

    private static string SanitizeEnhanceProgramFileName(string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            throw new InvalidOperationException("CreateFile requires a valid file name.");
        }

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            safeFileName = safeFileName.Replace(invalidChar, '_');
        }

        if (safeFileName is "." or "..")
        {
            throw new InvalidOperationException("CreateFile file name cannot be a relative path segment.");
        }

        return safeFileName;
    }

    private static string ExtractExecutablePath(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return rawValue;
        }

        var trimmed = rawValue.Trim();
        if (File.Exists(trimmed))
        {
            return trimmed;
        }

        foreach (var extension in new[] { ".exe", ".cmd", ".bat", ".dll", ".msc", ".cpl", ".ocx", ".ps1", ".vbs", ".js", ".hta" })
        {
            var index = trimmed.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var candidate = trimmed[..(index + extension.Length)];
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return trimmed;
    }

    private static byte[] ConvertToBinary(string value)
    {
        var compact = Regex.Replace(value, @"\s+", string.Empty);
        if (compact.Length == 0)
        {
            return [];
        }

        if (compact.Length % 2 != 0 || !Regex.IsMatch(compact, @"\A[0-9a-fA-F]+\z"))
        {
            throw new FormatException($"REG_BINARY value '{value}' is not valid hexadecimal byte data.");
        }

        var bytes = new byte[compact.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = Convert.ToByte(compact.Substring(index * 2, 2), 16);
        }

        return bytes;
    }

    private static bool ShouldIncludeNode(XElement element, string cultureName)
    {
        if (!HasRequiredFiles(element))
        {
            return false;
        }

        if (!MatchesOsVersion(element))
        {
            return false;
        }

        return MatchesCulture(element, cultureName);
    }

    internal static XElement? SelectLocalizedElementForWrite(IEnumerable<XElement> elements, string cultureName)
    {
        var candidates = elements.Where(IsValidLocalizedNode).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var normalizedCultureName = NormalizeEnhanceCultureName(cultureName);
        var exact = candidates.FirstOrDefault(element => HasExactNormalizedCulture(element, normalizedCultureName));
        if (exact is not null)
        {
            return exact;
        }

        var noCulture = candidates.FirstOrDefault(IsNoCultureNode);
        if (noCulture is not null)
        {
            return noCulture;
        }

        if (!IsChineseEnhanceCultureName(normalizedCultureName))
        {
            var english = candidates.FirstOrDefault(element => HasExactNormalizedCulture(element, "en-US"));
            if (english is not null)
            {
                return english;
            }
        }

        return candidates[0];
    }

    internal static IReadOnlyList<XElement> SelectLocalizedElementsForWrite(IEnumerable<XElement> elements, string cultureName)
    {
        var candidates = elements.Where(IsValidLocalizedNode).ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var normalizedCultureName = NormalizeEnhanceCultureName(cultureName);
        var selected = new List<XElement>();
        selected.AddRange(candidates.Where(IsNoCultureNode));
        selected.AddRange(candidates.Where(element => HasExactNormalizedCulture(element, normalizedCultureName)));

        return selected.Count > 0 ? selected : [candidates[0]];
    }

    private static bool IsValidLocalizedNode(XElement element)
        => HasRequiredFiles(element) && MatchesOsVersion(element);

    private static bool IsNoCultureNode(XElement element)
        => string.IsNullOrWhiteSpace(element.Element("Culture")?.Value);

    private static bool HasExactNormalizedCulture(XElement element, string cultureName)
    {
        var culture = element.Element("Culture")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(culture))
        {
            return false;
        }

        return TryNormalizeEnhanceCultureName(culture, out var normalizedElementCulture)
            && string.Equals(normalizedElementCulture, NormalizeEnhanceCultureName(cultureName), StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRequiredFiles(XElement element)
    {
        foreach (var fileElement in element.Elements("FileExists"))
        {
            var candidate = Environment.ExpandEnvironmentVariables(fileElement.Value.Trim());
            if (!File.Exists(candidate))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesCulture(XElement element, string cultureName)
    {
        var culture = element.Element("Culture")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(culture))
        {
            return true;
        }

        return TryNormalizeEnhanceCultureName(culture, out var normalizedElementCulture)
            && string.Equals(normalizedElementCulture, NormalizeEnhanceCultureName(cultureName), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesOsVersion(XElement element)
    {
        foreach (var versionElement in element.Elements("OSVersion"))
        {
            if (!Version.TryParse(versionElement.Value.Trim(), out var version))
            {
                continue;
            }

            var compare = versionElement.Attribute("Compare")?.Value?.Trim() ?? ">=";
            var current = Environment.OSVersion.Version.CompareTo(version);
            var matched = compare switch
            {
                ">" => current > 0,
                "<" => current < 0,
                "=" => current == 0,
                ">=" => current >= 0,
                "<=" => current <= 0,
                _ => true
            };

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeEnhanceCultureName(string? cultureName)
    {
        if (TryNormalizeEnhanceCultureName(cultureName, out var normalizedCultureName)
            || TryNormalizeEnhanceCultureName(CultureInfo.CurrentUICulture.Name, out normalizedCultureName))
        {
            return normalizedCultureName;
        }

        return "en-US";
    }

    private static bool TryNormalizeEnhanceCultureName(string? cultureName, out string normalizedCultureName)
    {
        normalizedCultureName = "en-US";
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return false;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName.Trim());
            normalizedCultureName = culture.Name switch
            {
                "zh-CN" or "zh-Hans" or "zh-SG" => "zh-CN",
                "zh-TW" or "zh-Hant" or "zh-HK" or "zh-MO" => "zh-TW",
                "zh" => "zh-CN",
                "en" or "en-US" => "en-US",
                _ => "en-US"
            };

            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static bool IsChineseEnhanceCultureName(string cultureName)
        => string.Equals(cultureName, "zh-CN", StringComparison.OrdinalIgnoreCase)
           || string.Equals(cultureName, "zh-TW", StringComparison.OrdinalIgnoreCase);

    private static string GetUniqueRegistryPath(string basePath, string keyName)
    {
        var candidate = $@"{basePath}\{keyName}";
        if (Registry.ClassesRoot.OpenSubKey(candidate, writable: false) is null)
        {
            return candidate;
        }

        for (var index = 2; index < 1000; index++)
        {
            var indexedCandidate = $@"{basePath}\{keyName} ({index})";
            if (Registry.ClassesRoot.OpenSubKey(indexedCandidate, writable: false) is null)
            {
                return indexedCandidate;
            }
        }

        throw new InvalidOperationException($"Unable to allocate a unique registry key name for {keyName}.");
    }

    private static string? ResolveEditableText(RegistryKey itemKey, string? defaultValue)
    {
        var muiVerb = itemKey.GetValue("MUIVerb")?.ToString();
        if (!string.IsNullOrWhiteSpace(muiVerb))
        {
            return ShellMetadataResolver.ResolveResourceString(muiVerb);
        }

        if (!HasMultiItemSubCommands(itemKey) && !string.IsNullOrWhiteSpace(defaultValue))
        {
            return ShellMetadataResolver.ResolveResourceString(defaultValue);
        }

        return null;
    }

    private static bool HasMultiItemSubCommands(RegistryKey itemKey)
    {
        var subCommands = itemKey.GetValue("SubCommands")?.ToString();
        if (!string.IsNullOrWhiteSpace(subCommands))
        {
            return true;
        }

        var extendedSubCommandsKey = itemKey.GetValue("ExtendedSubCommandsKey")?.ToString();
        return !string.IsNullOrWhiteSpace(extendedSubCommandsKey);
    }

    private static bool CanEditCommandText(RegistryKey itemKey, RegistryKey? commandKey)
    {
        if (HasMultiItemSubCommands(itemKey))
        {
            return false;
        }

        if (commandKey?.GetValue("DelegateExecute") is not null)
        {
            return false;
        }

        using var dropTargetKey = itemKey.OpenSubKey("DropTarget", writable: false);
        if (dropTargetKey?.GetValue("CLSID") is not null)
        {
            return false;
        }

        return itemKey.GetValue("ExplorerCommandHandler") is null;
    }

    private static bool CanEditDisplayText(ContextMenuEntry item)
    {
        if (item.EntryKind != ContextMenuEntryKind.ShellVerb)
        {
            return false;
        }

        return !HasMultiItemSubCommands(item.RegistryPath)
               && !string.Equals(item.KeyName, "open", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMultiItemSubCommands(string registryPath)
    {
        using var itemKey = Registry.ClassesRoot.OpenSubKey(registryPath, writable: false);
        return itemKey is not null && HasMultiItemSubCommands(itemKey);
    }

    private static PipeResponse CreateFailure(string message, ContextMenuEntry? item = null, string? errorCode = null)
    {
        return new PipeResponse
        {
            Success = false,
            Message = message,
            Item = item,
            ErrorCode = errorCode
        };
    }

    private List<string> ApplyRegistryWriteProtection(bool enable, BackendUserContext? userContext)
    {
        var errors = new List<string>();

        foreach (var relativePath in MonitoredRoots
                     .Select(static root => root.StableRelativePath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ApplyRegistryWriteProtection(RegistryHive.LocalMachine, relativePath, enable, errors);

            if (userContext is not null)
            {
                try
                {
                    using var userRoot = OpenUserRegistryRoot(userContext, writable: true);
                    ApplyRegistryWriteProtectionToUserKey(userRoot, relativePath, enable, errors);
                }
                catch (Exception ex)
                {
                    errors.Add($"Unable to apply protection to user registry: {ex.Message}");
                }
            }
            else
            {
                errors.Add("Unable to apply protection to user registry: caller user context is not available.");
            }
        }

        return errors;
    }

    private static RegistryKey OpenUserRegistryRoot(BackendUserContext userContext, bool writable)
    {
        if (string.IsNullOrWhiteSpace(userContext.Sid))
        {
            throw new InvalidOperationException("The frontend user SID is not available.");
        }

        return Registry.Users.OpenSubKey(userContext.Sid, writable)
            ?? throw new InvalidOperationException($"The registry hive for user {userContext.Sid} is not loaded.");
    }

    private static void ApplyRegistryWriteProtectionToUserKey(RegistryKey userRoot, string relativePath, bool enable, List<string> errors)
    {
        try
        {
            using var classesRoot = userRoot.OpenSubKey(@"Software\Classes", writable: false);
            if (classesRoot is null)
            {
                return;
            }

            using var key = classesRoot.OpenSubKey(
                relativePath,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ChangePermissions | RegistryRights.ReadKey);

            if (key is null)
            {
                return;
            }

            var security = key.GetAccessControl(AccessControlSections.Access);
            foreach (var rule in CreateProtectionRules())
            {
                if (enable)
                {
                    security.AddAccessRule(rule);
                }
                else
                {
                    security.RemoveAccessRuleSpecific(rule);
                }
            }

            key.SetAccessControl(security);
        }
        catch (UnauthorizedAccessException ex)
        {
            errors.Add($"Access denied to {relativePath} in user registry: {ex.Message}");
        }
        catch (SecurityException ex)
        {
            errors.Add($"Security error on {relativePath} in user registry: {ex.Message}");
        }
        catch (Exception ex)
        {
            errors.Add($"Error protecting {relativePath} in user registry: {ex.Message}");
        }
    }

    private static void ApplyRegistryWriteProtection(RegistryHive hive, string relativePath, bool enable, List<string> errors)
    {
        try
        {
            using var classesRoot = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = classesRoot.OpenSubKey(
                $@"Software\Classes\{relativePath}",
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ChangePermissions | RegistryRights.ReadKey);

            if (key is null)
            {
                return;
            }

            var security = key.GetAccessControl(AccessControlSections.Access);
            foreach (var rule in CreateProtectionRules())
            {
                if (enable)
                {
                    security.AddAccessRule(rule);
                }
                else
                {
                    security.RemoveAccessRuleSpecific(rule);
                }
            }

            key.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            errors.Add($"{hive}\\Software\\Classes\\{relativePath}: {ex.Message}");
        }
    }

    private static IEnumerable<RegistryAccessRule> CreateProtectionRules()
    {
        var rights = RegistryRights.CreateSubKey | RegistryRights.SetValue;
        yield return new RegistryAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            rights,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);

        yield return new RegistryAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            rights,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);

        yield return new RegistryAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            rights,
            InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Deny);

        yield return new RegistryAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            rights,
            InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Deny);
    }

    private static IEnumerable<RegistryRootDescriptor> GetSceneRoots(ContextMenuSceneKind sceneKind, string? scopeValue, BackendUserContext? userContext)
    {
        return sceneKind switch
        {
            ContextMenuSceneKind.LnkFile => CreateShellSceneRoots(ContextMenuCategory.File, "lnkfile"),
            ContextMenuSceneKind.UwpShortcut => CreateShellSceneRoots(ContextMenuCategory.File, "Launcher.ImmersiveApplication"),
            ContextMenuSceneKind.ExeFile => CreateShellSceneRoots(ContextMenuCategory.File, "exefile"),
            ContextMenuSceneKind.UnknownType => CreateShellSceneRoots(ContextMenuCategory.File, "Unknown"),
            ContextMenuSceneKind.CustomExtension => CreateCustomExtensionRoots(scopeValue, userContext),
            ContextMenuSceneKind.PerceivedType => CreatePerceivedTypeRoots(scopeValue),
            ContextMenuSceneKind.DirectoryType => CreateDirectoryTypeRoots(scopeValue),
            ContextMenuSceneKind.CustomRegistryPath => CreateCustomRegistryPathRoots(scopeValue),
            _ => []
        };
    }

    private static IEnumerable<RegistryRootDescriptor> CreateShellSceneRoots(
        ContextMenuCategory category,
        string basePath,
        RegistryRootInstanceScope instanceScope = RegistryRootInstanceScope.AllKnownInstances,
        string? diagnosticSource = null)
    {
        yield return new RegistryRootDescriptor(
            category,
            $@"{basePath}\shell",
            ContextMenuEntryKind.ShellVerb,
            InstanceScope: instanceScope,
            DiagnosticSource: diagnosticSource);
        yield return new RegistryRootDescriptor(
            category,
            $@"{basePath}\shellex\ContextMenuHandlers",
            ContextMenuEntryKind.ShellExtension,
            InstanceScope: instanceScope,
            DiagnosticSource: diagnosticSource);
        yield return new RegistryRootDescriptor(
            category,
            $@"{basePath}\shellex\-ContextMenuHandlers",
            ContextMenuEntryKind.ShellExtension,
            $@"{basePath}\shellex\ContextMenuHandlers",
            true,
            instanceScope,
            diagnosticSource);
    }

    private static IEnumerable<RegistryRootDescriptor> CreateCustomExtensionRoots(string? scopeValue, BackendUserContext? userContext)
    {
        foreach (var associatedRoot in ResolveAssociatedClassRootsForExtension(scopeValue, userContext))
        {
            foreach (var root in CreateShellSceneRoots(
                         ContextMenuCategory.File,
                         associatedRoot.ClassRoot,
                         RegistryRootInstanceScope.MachineAndFrontendUser,
                         associatedRoot.SourcesText))
            {
                yield return root;
            }
        }
    }

    private static IEnumerable<RegistryRootDescriptor> CreateRelatedFileTypeRoots(BackendUserContext? userContext)
    {
        var classRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectRelatedFileTypeClassRoots(Registry.LocalMachine, @"SOFTWARE\Classes", classRoots);
        if (userContext is not null)
        {
            CollectRelatedFileTypeClassRoots(Registry.Users, $@"{userContext.Sid}\Software\Classes", classRoots);
        }

        foreach (var classRoot in classRoots.OrderBy(static root => root, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var root in CreateShellSceneRoots(
                         ContextMenuCategory.File,
                         classRoot,
                         RegistryRootInstanceScope.MachineAndFrontendUser,
                         "FileTypeBatch"))
            {
                yield return root;
            }
        }
    }

    private static void CollectRelatedFileTypeClassRoots(
        RegistryKey hive,
        string classesBasePath,
        ISet<string> classRoots)
    {
        using var classesRoot = hive.OpenSubKey(classesBasePath, writable: false);
        if (classesRoot is null)
        {
            return;
        }

        foreach (var subKeyName in classesRoot.GetSubKeyNames())
        {
            if (string.Equals(subKeyName, "SystemFileAssociations", StringComparison.OrdinalIgnoreCase))
            {
                CollectSystemFileAssociationRoots(classesRoot, classRoots);
                continue;
            }

            if (!IsRelatedFileTypeClassRootName(subKeyName))
            {
                continue;
            }

            if (HasAnyContextMenuSubkeyInClassesRoot(hive, classesBasePath, subKeyName))
            {
                classRoots.Add(subKeyName);
            }
        }
    }

    private static void CollectSystemFileAssociationRoots(RegistryKey classesRoot, ISet<string> classRoots)
    {
        using var associationsRoot = classesRoot.OpenSubKey("SystemFileAssociations", writable: false);
        if (associationsRoot is null)
        {
            return;
        }

        foreach (var associationName in associationsRoot.GetSubKeyNames())
        {
            var classRoot = $@"SystemFileAssociations\{associationName}";
            if (HasAnyContextMenuSubkey(associationsRoot, associationName))
            {
                classRoots.Add(classRoot);
            }
        }
    }

    private static bool HasAnyContextMenuSubkey(RegistryKey root, string classRoot)
    {
        foreach (var relativeSubPath in ContextMenuSubRootRelativePaths)
        {
            using var key = root.OpenSubKey($@"{classRoot}\{relativeSubPath}", writable: false);
            if (key is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRelatedFileTypeClassRootName(string classRoot)
    {
        if (string.IsNullOrWhiteSpace(classRoot))
        {
            return false;
        }

        if (classRoot.StartsWith(".", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !classRoot.Contains('\\', StringComparison.Ordinal)
               && !FileTypeBatchExcludedClassRoots.Contains(classRoot);
    }

    private static readonly HashSet<string> FileTypeBatchExcludedClassRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "*",
        "AllFilesystemObjects",
        "Directory",
        "Directory.Background",
        "Drive",
        "Folder",
        "LibraryFolder",
        "UserLibraryFolder",
        "DesktopBackground",
        "CLSID",
        "Interface",
        "TypeLib",
        "AppID",
        "Applications",
        "PackagedCom",
        "Protocols"
    };

    private static IEnumerable<RegistryRootDescriptor> CreatePerceivedTypeRoots(string? scopeValue)
    {
        if (string.IsNullOrWhiteSpace(scopeValue))
        {
            yield break;
        }

        foreach (var root in CreateShellSceneRoots(ContextMenuCategory.File, $@"SystemFileAssociations\{scopeValue.Trim()}"))
        {
            yield return root;
        }
    }

    private static IEnumerable<RegistryRootDescriptor> CreateDirectoryTypeRoots(string? scopeValue)
    {
        if (string.IsNullOrWhiteSpace(scopeValue))
        {
            yield break;
        }

        foreach (var root in CreateShellSceneRoots(ContextMenuCategory.Directory, $@"SystemFileAssociations\Directory.{scopeValue.Trim()}"))
        {
            yield return root;
        }
    }

    private static IEnumerable<RegistryRootDescriptor> CreateCustomRegistryPathRoots(string? scopeValue)
    {
        var relativePath = NormalizeClassesRootRelativePath(scopeValue);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            yield break;
        }

        if (relativePath.EndsWith(@"\shell", StringComparison.OrdinalIgnoreCase))
        {
            yield return new RegistryRootDescriptor(ContextMenuCategory.File, relativePath, ContextMenuEntryKind.ShellVerb);
            yield break;
        }

        if (relativePath.EndsWith(@"\ContextMenuHandlers", StringComparison.OrdinalIgnoreCase))
        {
            yield return new RegistryRootDescriptor(ContextMenuCategory.File, relativePath, ContextMenuEntryKind.ShellExtension);
            yield break;
        }

        if (relativePath.EndsWith(@"\-ContextMenuHandlers", StringComparison.OrdinalIgnoreCase))
        {
            yield return new RegistryRootDescriptor(
                ContextMenuCategory.File,
                relativePath,
                ContextMenuEntryKind.ShellExtension,
                relativePath.Replace(@"\-ContextMenuHandlers", @"\ContextMenuHandlers", StringComparison.OrdinalIgnoreCase),
                true);
            yield break;
        }

        foreach (var root in CreateShellSceneRoots(ContextMenuCategory.File, relativePath))
        {
            yield return root;
        }
    }

    internal static IReadOnlyList<AssociatedClassRoot> ResolveAssociatedClassRootsForExtension(
        string? scopeValue,
        BackendUserContext? userContext)
    {
        var extension = NormalizeExtension(scopeValue);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return [];
        }

        var roots = new Dictionary<string, AssociatedClassRootBuilder>(StringComparer.OrdinalIgnoreCase);
        AddAssociatedClassRoot(roots, $@"SystemFileAssociations\{extension}", AssociatedClassRootSource.SystemFileAssociations);
        AddAssociatedClassRoot(roots, extension, AssociatedClassRootSource.ExtensionKey);

        if (userContext is not null)
        {
            using var userClasses = OpenUserClassesRootForRead(userContext);
            ReadExtensionDefaultProgId(userClasses, extension, AssociatedClassRootSource.DefaultProgIdUser, roots);
            ReadExtensionOpenWithProgIds(userClasses, extension, AssociatedClassRootSource.ExtensionOpenWithProgidsUser, roots);

            using var fileExtsKey = OpenUserFileExtsExtensionKey(userContext, extension);
            ReadUserChoiceProgId(fileExtsKey, roots);
            ReadFileExtsOpenWithProgIds(fileExtsKey, roots);
        }

        using (var machineClasses = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes", writable: false))
        {
            ReadExtensionDefaultProgId(machineClasses, extension, AssociatedClassRootSource.DefaultProgIdMachine, roots);
            ReadExtensionOpenWithProgIds(machineClasses, extension, AssociatedClassRootSource.ExtensionOpenWithProgidsMachine, roots);
        }

        return roots.Values
            .Where(builder => HasAnyContextMenuSubkey(builder.ClassRoot, userContext))
            .Select(static builder => builder.ToAssociatedClassRoot())
            .OrderBy(static root => root.ClassRoot, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ReadExtensionDefaultProgId(
        RegistryKey? classesRoot,
        string extension,
        AssociatedClassRootSource source,
        IDictionary<string, AssociatedClassRootBuilder> roots)
    {
        using var extensionKey = classesRoot?.OpenSubKey(extension, writable: false);
        var progId = extensionKey?.GetValue(null)?.ToString();
        AddAssociatedClassRoot(roots, progId, source);
    }

    private static void ReadExtensionOpenWithProgIds(
        RegistryKey? classesRoot,
        string extension,
        AssociatedClassRootSource source,
        IDictionary<string, AssociatedClassRootBuilder> roots)
    {
        using var openWithProgIdsKey = classesRoot?.OpenSubKey($@"{extension}\OpenWithProgids", writable: false);
        foreach (var progId in openWithProgIdsKey?.GetValueNames() ?? [])
        {
            AddAssociatedClassRoot(roots, progId, source);
        }
    }

    private static void ReadUserChoiceProgId(
        RegistryKey? fileExtsExtensionKey,
        IDictionary<string, AssociatedClassRootBuilder> roots)
    {
        using var userChoiceKey = fileExtsExtensionKey?.OpenSubKey("UserChoice", writable: false);
        AddAssociatedClassRoot(
            roots,
            userChoiceKey?.GetValue("ProgId")?.ToString(),
            AssociatedClassRootSource.FileExtsUserChoice);
    }

    private static void ReadFileExtsOpenWithProgIds(
        RegistryKey? fileExtsExtensionKey,
        IDictionary<string, AssociatedClassRootBuilder> roots)
    {
        using var openWithProgIdsKey = fileExtsExtensionKey?.OpenSubKey("OpenWithProgids", writable: false);
        foreach (var progId in openWithProgIdsKey?.GetValueNames() ?? [])
        {
            AddAssociatedClassRoot(roots, progId, AssociatedClassRootSource.FileExtsOpenWithProgids);
        }
    }

    private static void AddAssociatedClassRoot(
        IDictionary<string, AssociatedClassRootBuilder> roots,
        string? classRoot,
        AssociatedClassRootSource source)
    {
        var normalized = NormalizeClassesRootRelativePath(classRoot);
        if (string.IsNullOrWhiteSpace(normalized)
            || (normalized.Contains('\\', StringComparison.Ordinal)
                && source != AssociatedClassRootSource.SystemFileAssociations)
            || normalized.Equals("Applications", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!roots.TryGetValue(normalized, out var builder))
        {
            builder = new AssociatedClassRootBuilder(normalized);
            roots[normalized] = builder;
        }

        builder.AddSource(source);
    }

    private static bool HasAnyContextMenuSubkey(string classRoot, BackendUserContext? userContext)
    {
        return HasAnyContextMenuSubkeyInClassesRoot(Registry.LocalMachine, @"SOFTWARE\Classes", classRoot)
               || (userContext is not null
                   && HasAnyContextMenuSubkeyInClassesRoot(Registry.Users, $@"{userContext.Sid}\Software\Classes", classRoot));
    }

    private static bool HasAnyContextMenuSubkeyInClassesRoot(RegistryKey hive, string classesBasePath, string classRoot)
    {
        foreach (var relativeSubPath in ContextMenuSubRootRelativePaths)
        {
            using var key = hive.OpenSubKey($@"{classesBasePath}\{classRoot}\{relativeSubPath}", writable: false);
            if (key is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static RegistryKey? OpenUserClassesRootForRead(BackendUserContext userContext)
    {
        using var userBaseKey = Registry.Users.OpenSubKey(userContext.Sid, writable: false);
        return userBaseKey?.OpenSubKey(UserClassesPath, writable: false);
    }

    private static RegistryKey? OpenUserFileExtsExtensionKey(BackendUserContext userContext, string extension)
    {
        using var userBaseKey = Registry.Users.OpenSubKey(userContext.Sid, writable: false);
        return userBaseKey?.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}",
            writable: false);
    }

    private static string? NormalizeExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var extension = value.Trim();
        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }

        return extension;
    }

    private static bool IsRelatedFileTypeEntry(FileTypeBatchQuery query, ContextMenuEntry entry)
    {
        if (entry.IsWindows11ContextMenu || entry.EntryKind != query.EntryKind)
        {
            return false;
        }

        return query.EntryKind switch
        {
            ContextMenuEntryKind.ShellVerb => IsRelatedShellVerb(query, entry),
            ContextMenuEntryKind.ShellExtension => IsRelatedShellExtension(query, entry),
            _ => false
        };
    }

    private static bool IsRelatedShellVerb(FileTypeBatchQuery query, ContextMenuEntry entry)
    {
        if (string.IsNullOrWhiteSpace(query.KeyName)
            || string.IsNullOrWhiteSpace(entry.KeyName)
            || !string.Equals(query.KeyName, entry.KeyName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var queryExecutable = NormalizeExecutableIdentity(query.CommandExecutablePath);
        var entryExecutable = NormalizeExecutableIdentity(entry.FilePath)
                              ?? NormalizeExecutableIdentity(ExtractCommandExecutablePath(entry.CommandText));
        return !string.IsNullOrWhiteSpace(queryExecutable)
               && !string.IsNullOrWhiteSpace(entryExecutable)
               && string.Equals(queryExecutable, entryExecutable, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRelatedShellExtension(FileTypeBatchQuery query, ContextMenuEntry entry)
    {
        return !string.IsNullOrWhiteSpace(query.HandlerClsid)
               && !string.IsNullOrWhiteSpace(entry.HandlerClsid)
               && string.Equals(query.HandlerClsid, entry.HandlerClsid, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProtectedFileTypeDeleteItem(ContextMenuEntry entry)
    {
        return entry.Category == ContextMenuCategory.File
               && entry.EntryKind == ContextMenuEntryKind.ShellVerb
               && (string.Equals(entry.KeyName, "open", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(entry.KeyName, "edit", StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeExecutableIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(expanded))
        {
            return null;
        }

        try
        {
            return Path.IsPathRooted(expanded)
                ? Path.GetFullPath(expanded).TrimEnd('\\')
                : expanded;
        }
        catch
        {
            return expanded;
        }
    }

    private static string? ExtractCommandExecutablePath(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(commandText.Trim());
        if (expanded.StartsWith('"'))
        {
            var closingQuoteIndex = expanded.IndexOf('"', 1);
            if (closingQuoteIndex > 1)
            {
                return expanded[1..closingQuoteIndex];
            }
        }

        foreach (var extension in new[] { ".exe", ".dll" })
        {
            var extensionIndex = expanded.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (extensionIndex > 0)
            {
                return expanded[..(extensionIndex + extension.Length)].Trim().Trim('"');
            }
        }

        return null;
    }

    private const string UserClassesPath = @"Software\Classes";

    private static RegistryKey GetUserRegistryRoot(BackendUserContext context, bool writable)
    {
        if (string.IsNullOrWhiteSpace(context.Sid))
        {
            throw new InvalidOperationException("The frontend user SID is not available.");
        }

        return Registry.Users.OpenSubKey(context.Sid, writable)
            ?? throw new InvalidOperationException("The current user's registry hive is not available.");
    }

    private static RegistryKey GetUserClassesRoot(BackendUserContext context, bool writable)
    {
        var userBaseKey = GetUserRegistryRoot(context, writable: true);
        return userBaseKey.OpenSubKey(UserClassesPath, writable)
            ?? userBaseKey.CreateSubKey(UserClassesPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open or create the frontend user's Software\\Classes key.");
    }

    private static string ComposeUserClassesAbsolutePath(BackendUserContext context, string relativePath)
        => $@"HKEY_USERS\{context.Sid}\{UserClassesPath}\{relativePath.Trim('\\')}";

    private static RegistryKey CreateUserClassesSubKey(BackendUserContext context, string relativePath)
    {
        using var userClasses = GetUserClassesRoot(context, writable: true);
        return userClasses.CreateSubKey(relativePath, writable: true)
            ?? throw new InvalidOperationException(
                $"Unable to create per-user registry key: HKEY_USERS\\{context.Sid}\\{UserClassesPath}\\{relativePath}.");
    }

    private static RegistryKey? OpenUserClassesSubKey(BackendUserContext context, string relativePath, bool writable)
    {
        using var userClasses = GetUserClassesRoot(context, writable: true);
        return userClasses.OpenSubKey(relativePath, writable);
    }

    private static void DeleteUserClassesSubKeyTree(BackendUserContext context, string relativePath)
    {
        using var userClasses = GetUserClassesRoot(context, writable: true);
        try
        {
            userClasses.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
        }
        catch (ArgumentException)
        {
            // Key does not exist; nothing to delete.
        }
    }

    private static string? NormalizeClassesRootRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = value.Trim()
            .Replace('/', '\\')
            .Trim('\\');

        const string longPrefix = @"HKEY_CLASSES_ROOT\";
        const string shortPrefix = @"HKCR\";

        if (path.StartsWith(longPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[longPrefix.Length..];
        }
        else if (path.StartsWith(shortPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[shortPrefix.Length..];
        }

        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private async Task<ContextMenuEntry> SelectAndNormalizeActualEntryAsync(
        IReadOnlyList<ContextMenuEntry> entries,
        bool? desiredEnabled,
        bool repairDuplicateContainers,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 1)
        {
            return entries[0];
        }

        var activeEntries = entries
            .Where(static entry => entry.EntryKind == ContextMenuEntryKind.ShellExtension
                                   && !IsDisabledContainerEntry(entry))
            .ToArray();
        var disabledEntries = entries
            .Where(static entry => entry.EntryKind == ContextMenuEntryKind.ShellExtension
                                   && IsDisabledContainerEntry(entry))
            .ToArray();

        if (activeEntries.Length == 0 || disabledEntries.Length == 0)
        {
            // Multiple machine/user registrations on the same side are valid
            // physical copies of one logical item. Preserve the old selection
            // behavior and leave all of them intact.
            return entries[^1];
        }

        var hasActiveTimestamp = TryGetNewestRegistryWriteTimeUtc(activeEntries, out var newestActiveWriteUtc);
        var hasDisabledTimestamp = TryGetNewestRegistryWriteTimeUtc(disabledEntries, out var newestDisabledWriteUtc);
        var keepEnabled = SelectEnabledSideForDuplicate(
            hasActiveTimestamp ? newestActiveWriteUtc : null,
            hasDisabledTimestamp ? newestDisabledWriteUtc : null,
            desiredEnabled);
        var keptEntries = keepEnabled ? activeEntries : disabledEntries;
        var obsoleteEntries = keepEnabled ? disabledEntries : activeEntries;
        var selected = SelectNewestPhysicalEntry(keptEntries);

        if (!repairDuplicateContainers)
        {
            return selected;
        }

        using (var keptVerification = OpenRegistryKey(selected.BackendRegistryPath, writable: false))
        {
            if (keptVerification is null)
            {
                await _logger.LogAsync(
                    RuntimeLogLevel.Warning,
                    $"ClassicShellExtensionDuplicateAutoRepairFailed: ItemId={selected.Id}, KeptEnabled={keepEnabled}, " +
                    $"KeptPath={selected.BackendRegistryPath}, ObsoletePath=<none>, Exception=The selected newer registration disappeared before cleanup.",
                    cancellationToken);
                return selected;
            }
        }

        var removedPaths = new List<string>();
        var failedPaths = new List<string>();
        foreach (var obsolete in obsoleteEntries)
        {
            try
            {
                DeleteRegistryKeyTree(obsolete.BackendRegistryPath);
                using var verification = OpenRegistryKey(obsolete.BackendRegistryPath, writable: false);
                if (verification is not null)
                {
                    throw new InvalidOperationException("The obsolete registration still exists after deletion.");
                }

                removedPaths.Add(obsolete.BackendRegistryPath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
            {
                failedPaths.Add(obsolete.BackendRegistryPath);
                await _logger.LogAsync(
                    RuntimeLogLevel.Warning,
                    $"ClassicShellExtensionDuplicateAutoRepairFailed: ItemId={selected.Id}, KeptEnabled={keepEnabled}, " +
                    $"KeptPath={selected.BackendRegistryPath}, ObsoletePath={obsolete.BackendRegistryPath}, Exception={ex.Message}.",
                    cancellationToken);
            }
        }

        if (removedPaths.Count > 0)
        {
            var selectionReason = hasActiveTimestamp && hasDisabledTimestamp
                && newestActiveWriteUtc != newestDisabledWriteUtc
                    ? "LastWriteTime"
                    : desiredEnabled is not null
                        ? "PersistedDesiredStateFallback"
                        : "ActiveContainerFallback";
            await _logger.LogAsync(
                $"ClassicShellExtensionDuplicateAutoRepaired: ItemId={selected.Id}, KeptEnabled={keepEnabled}, " +
                $"SelectionReason={selectionReason}, ActiveLastWriteUtc={FormatRegistryWriteTime(hasActiveTimestamp, newestActiveWriteUtc)}, " +
                $"DisabledLastWriteUtc={FormatRegistryWriteTime(hasDisabledTimestamp, newestDisabledWriteUtc)}, " +
                $"KeptPath={selected.BackendRegistryPath}, RemovedPaths={string.Join(";", removedPaths)}, " +
                $"FailedPaths={string.Join(";", failedPaths)}.",
                cancellationToken);
            ShellChangeNotifier.NotifyAssociationsChanged();
        }

        // The newer physical side is authoritative. A failed cleanup is retried
        // on the next persisted snapshot and is logged, but it is not presented
        // as the misleading saved-state consistency warning.
        return selected with
        {
            HasConsistencyIssue = false,
            ConsistencyIssue = null
        };
    }

    internal static bool SelectEnabledSideForDuplicate(
        DateTimeOffset? newestActiveWriteUtc,
        DateTimeOffset? newestDisabledWriteUtc,
        bool? desiredEnabled)
    {
        if (newestActiveWriteUtc is not null
            && newestDisabledWriteUtc is not null
            && newestActiveWriteUtc != newestDisabledWriteUtc)
        {
            return newestActiveWriteUtc > newestDisabledWriteUtc;
        }

        return desiredEnabled ?? true;
    }

    private static ContextMenuEntry SelectNewestPhysicalEntry(IReadOnlyList<ContextMenuEntry> entries)
    {
        ContextMenuEntry? selected = null;
        DateTimeOffset? selectedWriteUtc = null;
        foreach (var entry in entries)
        {
            if (!TryGetRegistryWriteTimeUtc(entry.BackendRegistryPath, out var writeUtc))
            {
                selected ??= entry;
                continue;
            }

            if (selectedWriteUtc is null || writeUtc > selectedWriteUtc)
            {
                selected = entry;
                selectedWriteUtc = writeUtc;
            }
        }

        return selected ?? entries[^1];
    }

    private static bool TryGetNewestRegistryWriteTimeUtc(
        IReadOnlyList<ContextMenuEntry> entries,
        out DateTimeOffset newestWriteUtc)
    {
        newestWriteUtc = default;
        foreach (var entry in entries)
        {
            if (!TryGetRegistryWriteTimeUtc(entry.BackendRegistryPath, out var writeUtc))
            {
                return false;
            }

            if (writeUtc > newestWriteUtc)
            {
                newestWriteUtc = writeUtc;
            }
        }

        return entries.Count > 0;
    }

    internal static bool TryGetRegistryWriteTimeUtc(string registryPath, out DateTimeOffset writeUtc)
    {
        writeUtc = default;
        try
        {
            using var key = OpenRegistryKey(registryPath, writable: false);
            if (key is null
                || RegQueryInfoKey(
                    key.Handle.DangerousGetHandle(),
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out var lastWriteFileTime) != 0)
            {
                return false;
            }

            writeUtc = DateTimeOffset.FromFileTime(lastWriteFileTime).ToUniversalTime();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static string FormatRegistryWriteTime(bool available, DateTimeOffset value)
        => available ? value.ToString("O", CultureInfo.InvariantCulture) : "<unavailable>";

    private static ContextMenuEntry? SelectPreferredDeleteCandidate(IEnumerable<ContextMenuEntry> candidates)
    {
        return candidates
            .Where(static entry => entry.IsPresentInRegistry && !entry.IsDeleted)
            .OrderBy(static entry => entry.BackendRegistryPath.StartsWith(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(static entry => IsDisabledContainerEntry(entry) ? 1 : 0)
            .FirstOrDefault();
    }

    private static bool IsWpsOfficeSyntheticId(string? itemId)
        => !string.IsNullOrWhiteSpace(itemId)
           && itemId.StartsWith("special:wps-", StringComparison.OrdinalIgnoreCase);

    internal static bool HasWpsOfficeSyntheticBaseline(IEnumerable<PersistedContextMenuState> states)
        => states.Any(static state => !state.IsDeleted && IsWpsOfficeSyntheticSource(state.SourceRootPath));

    private static bool IsWpsOfficeSyntheticSource(string? sourceRootPath)
        => string.Equals(sourceRootPath, "special:wps-office-coexistence", StringComparison.OrdinalIgnoreCase);

    private static bool IsDisabledContainerEntry(ContextMenuEntry entry)
    {
        return entry.RegistryPath.Contains(@"\-ContextMenuHandlers\", StringComparison.OrdinalIgnoreCase)
               || entry.BackendRegistryPath.Contains(@"\-ContextMenuHandlers\", StringComparison.OrdinalIgnoreCase);
    }

    private async Task LogCustomExtensionSceneDiagnosticsAsync(
        ContextMenuSceneKind sceneKind,
        string? scopeValue,
        BackendUserContext? userContext,
        IReadOnlyList<RegistryRootDescriptor> roots,
        IReadOnlyList<ContextMenuEntry> entries,
        CancellationToken cancellationToken)
    {
        if (sceneKind != ContextMenuSceneKind.CustomExtension)
        {
            return;
        }

        var extension = NormalizeExtension(scopeValue) ?? string.Empty;
        var associatedRoots = roots
            .Select(static root => TrimContextMenuSubRoot(root.StableRelativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static root => root, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var rootSources = roots
            .GroupBy(static root => TrimContextMenuSubRoot(root.StableRelativePath), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var sources = group
                    .Select(static root => root.DiagnosticSource)
                    .Where(static source => !string.IsNullOrWhiteSpace(source))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                var count = entries.Count(entry => group.Any(root => string.Equals(entry.SourceRootPath, root.StableRelativePath, StringComparison.OrdinalIgnoreCase)));
                var itemKeys = entries
                    .Where(entry => group.Any(root => string.Equals(entry.SourceRootPath, root.StableRelativePath, StringComparison.OrdinalIgnoreCase)))
                    .Select(static entry => entry.KeyName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase);
                return $"Root={group.Key}, Sources={string.Join("|", sources)}, EntryCount={count}, Items={string.Join("|", itemKeys)}";
            });

        await _logger.LogAsync(
            $"CustomExtensionSceneSnapshot: Extension={extension}, FrontendSid={DiagnosticLogFormatter.FormatSid(userContext)}, AssociatedRoots={string.Join("|", associatedRoots)}, RootDetails=[{string.Join("; ", rootSources)}].",
            cancellationToken);
    }

    private static string TrimContextMenuSubRoot(string relativePath)
    {
        foreach (var suffix in ContextMenuSubRootRelativePaths)
        {
            if (relativePath.EndsWith($@"\{suffix}", StringComparison.OrdinalIgnoreCase))
            {
                return relativePath[..^(suffix.Length + 1)];
            }
        }

        return relativePath;
    }

    private static ContextMenuCategory DetermineCategoryFromPath(string stableRelativePath)
    {
        var match = MonitoredRoots.FirstOrDefault(root =>
            stableRelativePath.StartsWith(root.StableRelativePath, StringComparison.OrdinalIgnoreCase)
            || stableRelativePath.StartsWith(root.RelativePath, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            return match.Category;
        }

        if (stableRelativePath.StartsWith(@"Directory\Background", StringComparison.OrdinalIgnoreCase))
        {
            return ContextMenuCategory.DirectoryBackground;
        }

        if (stableRelativePath.StartsWith(@"DesktopBackground", StringComparison.OrdinalIgnoreCase))
        {
            return ContextMenuCategory.DesktopBackground;
        }

        if (stableRelativePath.StartsWith(@"Drive", StringComparison.OrdinalIgnoreCase))
        {
            return ContextMenuCategory.Drive;
        }

        if (stableRelativePath.StartsWith(@"LibraryFolder", StringComparison.OrdinalIgnoreCase)
            || stableRelativePath.StartsWith(@"UserLibraryFolder", StringComparison.OrdinalIgnoreCase))
        {
            return ContextMenuCategory.Library;
        }

        return ContextMenuCategory.File;
    }

    private static IEnumerable<RegistryRootInstance> EnumerateRootInstances(
        RegistryRootInstanceScope scope = RegistryRootInstanceScope.AllKnownInstances,
        BackendUserContext? userContext = null)
    {
        yield return new RegistryRootInstance(
            Registry.LocalMachine,
            @"SOFTWARE\Classes",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes");

        if (scope == RegistryRootInstanceScope.MachineAndFrontendUser)
        {
            if (userContext is not null)
            {
                yield return new RegistryRootInstance(
                    Registry.Users,
                    $@"{userContext.Sid}\Software\Classes",
                    $@"HKEY_USERS\{userContext.Sid}\Software\Classes");
            }

            yield break;
        }

        foreach (var userSid in Registry.Users.GetSubKeyNames()
                     .Where(static sid => sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
                                          && !sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(static sid => sid, StringComparer.OrdinalIgnoreCase))
        {
            yield return new RegistryRootInstance(
                Registry.Users,
                $@"{userSid}\Software\Classes",
                $@"HKEY_USERS\{userSid}\Software\Classes");
        }
    }

    private static void WriteDetailedEditRegistryValue(
        string fullPath,
        string keyName,
        string? valueKind,
        string? value,
        string? userSid)
    {
        var kind = ParseDetailedEditRegistryValueKind(valueKind);
        var (baseKey, subPath) = OpenDetailedEditRegistryBaseKey(fullPath, userSid);
        using var key = baseKey.CreateSubKey(subPath, writable: true)
            ?? throw new InvalidOperationException($"Unable to open {fullPath} for writing.");

        if (value is null)
        {
            key.DeleteValue(keyName, throwOnMissingValue: false);
            return;
        }

        object boxedValue = kind switch
        {
            RegistryValueKind.DWord => int.Parse(value, CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => long.Parse(value, CultureInfo.InvariantCulture),
            RegistryValueKind.Binary => ConvertToBinary(value),
            RegistryValueKind.MultiString => value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => value
        };

        key.SetValue(keyName, boxedValue, kind);
    }

    private static RegistryValueKind ParseDetailedEditRegistryValueKind(string? valueKind)
    {
        if (string.IsNullOrWhiteSpace(valueKind))
        {
            return RegistryValueKind.String;
        }

        return Enum.TryParse<RegistryValueKind>(valueKind, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Unsupported registry value kind: {valueKind}");
    }

    private static (RegistryKey BaseKey, string SubPath) OpenDetailedEditRegistryBaseKey(
        string fullPath,
        string? userSid)
    {
        var normalized = fullPath.Replace('/', '\\').Trim();
        var separatorIndex = normalized.IndexOf('\\');
        var root = separatorIndex >= 0 ? normalized[..separatorIndex] : normalized;
        var subPath = separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : string.Empty;

        return root.ToUpperInvariant() switch
        {
            "HKEY_CLASSES_ROOT" or "HKCR" => (Registry.ClassesRoot, subPath),
            "HKEY_CURRENT_USER" or "HKCU" when !string.IsNullOrWhiteSpace(userSid)
                => (Registry.Users, $@"{userSid}\{subPath}"),
            "HKEY_CURRENT_USER" or "HKCU"
                => throw new InvalidOperationException("HKCU detailed edit writes require the frontend user SID."),
            "HKEY_LOCAL_MACHINE" or "HKLM" => (Registry.LocalMachine, subPath),
            "HKEY_USERS" or "HKU" => (Registry.Users, subPath),
            _ => throw new InvalidOperationException($"Unsupported registry root: {fullPath}")
        };
    }

    private static RegistryKey? OpenRegistryKey(string absoluteRegistryPath, bool writable)
    {
        if (TrySplitAbsoluteRegistryPath(absoluteRegistryPath, out var rootKey, out var subPath))
        {
            return rootKey.OpenSubKey(subPath, writable);
        }

        return Registry.ClassesRoot.OpenSubKey(absoluteRegistryPath, writable);
    }

    private static RegistryKey? CreateRegistrySubKey(string absoluteRegistryPath, bool writable)
    {
        if (TrySplitAbsoluteRegistryPath(absoluteRegistryPath, out var rootKey, out var subPath))
        {
            return rootKey.CreateSubKey(subPath, writable);
        }

        return Registry.ClassesRoot.CreateSubKey(absoluteRegistryPath, writable);
    }

    private static void DeleteRegistryKeyTree(string absoluteRegistryPath)
    {
        if (TrySplitAbsoluteRegistryPath(absoluteRegistryPath, out var rootKey, out var subPath))
        {
            rootKey.DeleteSubKeyTree(subPath, throwOnMissingSubKey: false);
            return;
        }

        Registry.ClassesRoot.DeleteSubKeyTree(absoluteRegistryPath, throwOnMissingSubKey: false);
    }

    private static bool TrySplitAbsoluteRegistryPath(string absoluteRegistryPath, out RegistryKey rootKey, out string subPath)
    {
        rootKey = null!;
        subPath = string.Empty;

        if (string.IsNullOrWhiteSpace(absoluteRegistryPath))
        {
            return false;
        }

        var normalized = absoluteRegistryPath.Trim();
        foreach (var pair in new (string Prefix, RegistryKey Key)[]
                 {
                     (@"HKEY_LOCAL_MACHINE\", Registry.LocalMachine),
                     (@"HKLM\", Registry.LocalMachine),
                     (@"HKEY_USERS\", Registry.Users),
                     (@"HKU\", Registry.Users),
                     (@"HKEY_CLASSES_ROOT\", Registry.ClassesRoot),
                     (@"HKCR\", Registry.ClassesRoot)
                 })
        {
            if (!normalized.StartsWith(pair.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rootKey = pair.Key;
            subPath = normalized[pair.Prefix.Length..];
            return true;
        }

        return false;
    }

    private sealed record RegistryRootDescriptor(
        ContextMenuCategory Category,
        string RelativePath,
        ContextMenuEntryKind EntryKind,
        string? StableRelativePath = null,
        bool IsDisabledContainer = false,
        RegistryRootInstanceScope InstanceScope = RegistryRootInstanceScope.AllKnownInstances,
        string? DiagnosticSource = null)
    {
        /// <summary>
        /// Gets the stable Relative Path.
        /// </summary>
        public string StableRelativePath { get; } = StableRelativePath ?? RelativePath;
    }

    internal sealed record AssociatedClassRoot(string ClassRoot, IReadOnlyList<AssociatedClassRootSource> Sources)
    {
        public string SourcesText => string.Join("|", Sources);
    }

    internal enum AssociatedClassRootSource
    {
        SystemFileAssociations,
        ExtensionKey,
        DefaultProgIdUser,
        DefaultProgIdMachine,
        ExtensionOpenWithProgidsUser,
        ExtensionOpenWithProgidsMachine,
        FileExtsUserChoice,
        FileExtsOpenWithProgids
    }

    private enum RegistryRootInstanceScope
    {
        AllKnownInstances,
        MachineAndFrontendUser
    }

    private sealed class AssociatedClassRootBuilder
    {
        private readonly List<AssociatedClassRootSource> _sources = [];

        public AssociatedClassRootBuilder(string classRoot)
        {
            ClassRoot = classRoot;
        }

        public string ClassRoot { get; }

        public void AddSource(AssociatedClassRootSource source)
        {
            if (!_sources.Contains(source))
            {
                _sources.Add(source);
            }
        }

        public AssociatedClassRoot ToAssociatedClassRoot()
            => new(ClassRoot, _sources.ToArray());
    }

    private sealed record RegistryRootInstance(
        RegistryKey Hive,
        string ClassesBasePath,
        string AbsoluteRootPath)
    {
        /// <summary>
        /// Opens base Key.
        /// </summary>
        public RegistryKey? OpenBaseKey(string relativePath) => Hive.OpenSubKey($@"{ClassesBasePath}\{relativePath}", writable: false);

        /// <summary>
        /// Executes compose Absolute Path.
        /// </summary>
        public string ComposeAbsolutePath(string relativePath) => $@"{AbsoluteRootPath}\{relativePath}";
    }

    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_READ = 0x0008;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;
    private const string SE_BACKUP_NAME = "SeBackupPrivilege";

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public LUID_AND_ATTRIBUTES[] Privileges;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool LookupPrivilegeValue(string? systemName, string privilegeName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, ref uint returnLength);

    [DllImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKey(
        IntPtr key,
        StringBuilder? keyClass,
        IntPtr keyClassLength,
        IntPtr reserved,
        IntPtr subKeyCount,
        IntPtr maxSubKeyNameLength,
        IntPtr maxClassLength,
        IntPtr valueCount,
        IntPtr maxValueNameLength,
        IntPtr maxValueLength,
        IntPtr securityDescriptorLength,
        out long lastWriteFileTime);

    private static bool EnableBackupPrivilege()
    {
        try
        {
            IntPtr tokenHandle;
            var processHandle = System.Diagnostics.Process.GetCurrentProcess().SafeHandle;
            if (!OpenProcessToken(processHandle.DangerousGetHandle(), TOKEN_ADJUST_PRIVILEGES | TOKEN_READ, out tokenHandle))
            {
                return false;
            }

            try
            {
                var privilege = new LUID_AND_ATTRIBUTES
                {
                    Luid = new LUID(),
                    Attributes = SE_PRIVILEGE_ENABLED
                };

                if (!LookupPrivilegeValue(null, SE_BACKUP_NAME, out privilege.Luid))
                {
                    return false;
                }

                var privileges = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = [privilege]
                };

                var length = 0u;
                if (!AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0u, IntPtr.Zero, ref length))
                {
                    return false;
                }

                return Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteRegistrySubKeyTreeWithFallback(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        EnableBackupPrivilege();

        try
        {
            DeleteRegistryKeyTree(fullPath);
            return;
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (TrySplitAbsoluteRegistryPath(fullPath, out var rootKey, out var subPath))
        {
            var parentPath = subPath.Contains('\\') ? subPath[..subPath.LastIndexOf('\\')] : string.Empty;
            var keyName = subPath.Contains('\\') ? subPath[(subPath.LastIndexOf('\\') + 1)..] : subPath;

            if (rootKey == Registry.ClassesRoot)
            {
                var machineRoot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes", writable: true);
                if (machineRoot is not null)
                {
                    using var parent = machineRoot.OpenSubKey(parentPath, writable: true);
                    parent?.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
                    return;
                }
            }
        }
    }
}

internal static class ShellChangeNotifier
{
    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    public static void NotifyAssociationsChanged()
    {
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}

/// <summary>
/// Represents the result of a disabled-state reconciliation pass.
/// </summary>
public sealed record DisabledStateReconciliationResult(
    bool HasChanges,
    IReadOnlyList<string> ReconciledItemIds,
    IReadOnlyList<string> FailedItemIds);
