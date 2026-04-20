using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using System.IO;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Represents the context Menu Registry Catalog.
/// </summary>
public sealed class ContextMenuRegistryCatalog
{
    private const string BlockedShellExtensionsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    internal const string Windows11MonitoredRootPath = @"PackagedCom\Windows11ContextMenu";
    private static readonly TimeSpan StartupMissingStatePreservationWindow = TimeSpan.FromMinutes(2);

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
    private readonly DateTimeOffset _catalogStartedAtUtc = DateTimeOffset.UtcNow;

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
    }

    /// <summary>
    /// Gets snapshot Async.
    /// </summary>
    public async Task<IReadOnlyList<ContextMenuEntry>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return await BuildSnapshotAsync(
            await EnumerateActualEntriesAsync(cancellationToken),
            static state => MonitoredStableRootPaths.Contains(state.SourceRootPath) || state.IsWindows11ContextMenu,
            persistDiscoveredStates: true,
            cancellationToken);
    }

    /// <summary>
    /// Gets scene Snapshot Async.
    /// </summary>
    public async Task<IReadOnlyList<ContextMenuEntry>> GetSceneSnapshotAsync(
        ContextMenuSceneKind sceneKind,
        string? scopeValue,
        CancellationToken cancellationToken = default)
    {
        var roots = GetSceneRoots(sceneKind, scopeValue).ToArray();
        if (roots.Length == 0)
        {
            return [];
        }

        var includedRootPaths = roots
            .Select(static root => root.StableRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var snapshot = await BuildSnapshotAsync(
            EnumerateEntries(roots),
            state => includedRootPaths.Contains(state.SourceRootPath),
            persistDiscoveredStates: false,
            cancellationToken);

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

    private async Task<IReadOnlyList<ContextMenuEntry>> BuildSnapshotAsync(
        IEnumerable<ContextMenuEntry> actualEntriesSource,
        Func<PersistedContextMenuState, bool> includePersistedState,
        bool persistDiscoveredStates,
        CancellationToken cancellationToken)
    {
        var states = await _stateStore.LoadAsync(cancellationToken);
        var hasBaseline = states.Count > 0;
        var actualEntries = new Dictionary<string, ContextMenuEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in actualEntriesSource)
        {
            if (actualEntries.TryGetValue(item.Id, out var existing))
            {
                // Some roots can describe the same logical menu entry more than once
                // (for example stable vs. mirrored shell extension locations).
                actualEntries[item.Id] = SelectPreferredActualEntry(existing, item);
                continue;
            }

            actualEntries[item.Id] = item;
        }

        var results = new List<ContextMenuEntry>();
        var dirty = false;
        var missingStateIdsToRemove = new List<string>();

        foreach (var entry in actualEntries.Values.OrderBy(static item => item.Category).ThenBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            states.TryGetValue(entry.Id, out var state);
            var issue = GetConsistencyIssue(entry, state);
            var changeKind = GetDetectedChangeKind(entry, state, hasBaseline);
            var changeDetails = GetDetectedChangeDetails(entry, state, changeKind);
            var merged = entry with
            {
                IsPendingApproval = state?.IsPendingApproval ?? false,
                HasBackup = !string.IsNullOrWhiteSpace(state?.BackupFilePath),
                DeletedAtUtc = state?.DeletedAtUtc,
                IsPresentInRegistry = true,
                HasConsistencyIssue = !string.IsNullOrWhiteSpace(issue),
                ConsistencyIssue = issue,
                DetectedChangeKind = changeKind,
                DetectedChangeDetails = changeDetails
            };

            results.Add(merged);

            if (state is null)
            {
                if (persistDiscoveredStates)
                {
                    // The first persisted snapshot becomes the baseline that later
                    // runs compare against for change detection and approvals.
                    states[entry.Id] = PersistedContextMenuState.FromEntry(merged);
                    dirty = true;
                }

                continue;
            }

            dirty |= UpdateMetadata(state, merged);
        }

        foreach (var state in states.Values
                     .Where(state => includePersistedState(state) && !actualEntries.ContainsKey(state.Id))
                     .OrderBy(static state => state.Category)
                     .ThenBy(static state => state.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (!state.IsDeleted && IsWithinStartupMissingStatePreservationWindow())
            {
                // Service startup can happen before all per-user classes and packaged
                // COM registrations are fully visible. Keeping the baseline alive for
                // a short startup window prevents the entire catalog from being
                // re-quarantined as "new" once those registrations appear.
                continue;
            }

            // External removals are intentionally silent in the UI, but they still
            // need to be removed from the persisted baseline. Otherwise a later
            // reinstall looks like an old known item instead of a genuinely new one.
            if (!state.IsDeleted)
            {
                missingStateIdsToRemove.Add(state.Id);
                dirty = true;
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

        PruneTransientStates(states);

        if (dirty)
        {
            await _stateStore.SaveAsync(states, cancellationToken);
        }

        return results;
    }

    private bool IsWithinStartupMissingStatePreservationWindow()
    {
        return DateTimeOffset.UtcNow - _catalogStartedAtUtc < StartupMissingStatePreservationWindow;
    }

    /// <summary>
    /// Applies desired State Async.
    /// </summary>
    public async Task<PipeResponse> ApplyDesiredStateAsync(string itemId, bool enable, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var item = snapshot.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return CreateFailure($"Menu item '{itemId}' was not found.");
        }

        if (item.IsDeleted)
        {
            return CreateFailure($"Menu item '{item.DisplayName}' is deleted. Undo the deletion before changing its state.");
        }

        try
        {
            if (item.IsWindows11ContextMenu)
            {
                // Win11 packaged verbs do not use the classic shell verb/handler
                // write paths, so they are toggled through the blocked-extension list.
                if (!_windows11Catalog.SetEnabled(item.HandlerClsid ?? item.KeyName, item.DisplayName, null, enable))
                {
                    return CreateFailure($"Unable to update the Win11 context menu item '{item.DisplayName}'.");
                }
            }
            else
            switch (item.EntryKind)
            {
                case ContextMenuEntryKind.ShellVerb:
                    SetShellVerbEnabled(item.BackendRegistryPath, enable);
                    break;
                case ContextMenuEntryKind.ShellExtension:
                    SetShellExtensionEnabled(item, enable);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported entry kind: {item.EntryKind}");
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

            if (item.EntryKind == ContextMenuEntryKind.ShellExtension)
            {
                UpdateLinkedShellExtensionPersistedStates(states, item.HandlerClsid, enable);
            }

            PruneTransientStates(states);
            await _stateStore.SaveAsync(states, cancellationToken);

            await _logger.LogAsync($"{(enable ? "Enabled" : "Disabled")} {item.DisplayName} ({item.RegistryPath}).", cancellationToken);

            var refreshed = (await GetSnapshotAsync(cancellationToken))
                .FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase))
                ?? item with { IsEnabled = enable };

            return new PipeResponse
            {
                Success = true,
                Message = $"{(enable ? "Enabled" : "Disabled")} {item.DisplayName}.",
                Item = refreshed
            };
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
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var item = snapshot.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase));

        return decision switch
        {
            ContextMenuDecision.Allow => item is null
                ? CreateFailure($"Menu item '{itemId}' was not found.")
                : await ApplyDesiredStateAsync(itemId, enable: true, cancellationToken),
            ContextMenuDecision.Deny => item is null
                ? await RemovePendingApprovalStateAsync(itemId, cancellationToken)
                : await ApplyDesiredStateAsync(itemId, enable: false, cancellationToken),
            ContextMenuDecision.Remove => await RemovePendingApprovalItemAsync(item, itemId, cancellationToken),
            _ => CreateFailure("Unknown approval decision.")
        };
    }

    /// <summary>
    /// Executes acknowledge Item State Async.
    /// </summary>
    public async Task<PipeResponse> AcknowledgeItemStateAsync(string itemId, CancellationToken cancellationToken)
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
            state.EditableText = parsedText;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _stateStore.SaveAsync(states, cancellationToken);

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

    /// <summary>
    /// Sets enhance Menu Item Enabled Async.
    /// </summary>
    public async Task<PipeResponse> SetEnhanceMenuItemEnabledAsync(
        string groupRegistryPath,
        string definitionXml,
        bool enable,
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

        try
        {
            var itemElement = XElement.Parse(definitionXml);
            var relativeGroupPath = NormalizeClassesRootRelativePath(groupRegistryPath)
                ?? throw new InvalidOperationException("The enhance-menu group path must point into HKCR.");
            var states = await _stateStore.LoadAsync(cancellationToken);

            if (itemElement.Attribute("KeyName") is not null)
            {
                SetEnhanceShellItemEnabled(relativeGroupPath, itemElement, enable);
            }
            else if (itemElement.Element("Guid") is not null)
            {
                SetEnhanceShellExItemEnabled(relativeGroupPath, itemElement, enable);
            }
            else
            {
                throw new InvalidOperationException("The enhance-menu item definition could not be recognized.");
            }

            await SyncEnhanceMenuStateAsync(states, relativeGroupPath, itemElement, enable, cancellationToken);

            await _logger.LogAsync(
                $"{(enable ? "Enabled" : "Disabled")} enhance menu item under {groupRegistryPath}.",
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

    private async Task SyncEnhanceMenuStateAsync(
        Dictionary<string, PersistedContextMenuState> states,
        string relativeGroupPath,
        XElement itemElement,
        bool enable,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
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
    public async Task<PipeResponse> SetRegistryProtectionSettingAsync(bool enable, CancellationToken cancellationToken)
    {
        var errors = ApplyRegistryWriteProtection(enable);
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
    public async Task<PipeResponse> DeleteItemAsync(string itemId, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        var item = snapshot.FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return CreateFailure($"Menu item '{itemId}' was not found.");
        }

        if (item.IsDeleted)
        {
            return CreateFailure($"Menu item '{item.DisplayName}' is already deleted.", item);
        }

        if (!item.IsPresentInRegistry)
        {
            return await RemoveMissingItemStateAsync(item, cancellationToken);
        }

        try
        {
            var backupFilePath = await _backupService.ExportKeyAsync(item.BackendRegistryPath, cancellationToken);
            DeleteRegistryKey(item.BackendRegistryPath);

            var states = await _stateStore.LoadAsync(cancellationToken);
            var state = GetOrCreateState(states, item);
            state.DesiredEnabled = null;
            state.IsDeleted = true;
            state.IsPendingApproval = false;
            state.BackupFilePath = backupFilePath;
            state.DeletedAtUtc = DateTimeOffset.UtcNow;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _stateStore.SaveAsync(states, cancellationToken);

            await _logger.LogAsync($"Deleted {item.DisplayName} with backup {backupFilePath}.", cancellationToken);

            var refreshed = (await GetSnapshotAsync(cancellationToken))
                .FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase))
                ?? CreateVirtualEntry(state, null, ContextMenuChangeKind.None, null);

            return new PipeResponse
            {
                Success = true,
                Message = $"Deleted {item.DisplayName}.",
                Item = refreshed
            };
        }
        catch (Exception ex)
        {
            await _logger.LogAsync($"Failed to delete {item.DisplayName}: {ex.Message}", cancellationToken);
            return CreateFailure(ex.Message, item);
        }
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
    public async Task<PipeResponse> UndoDeleteAsync(string itemId, CancellationToken cancellationToken)
    {
        var states = await _stateStore.LoadAsync(cancellationToken);
        if (!states.TryGetValue(itemId, out var state) || !state.IsDeleted || string.IsNullOrWhiteSpace(state.BackupFilePath))
        {
            return CreateFailure($"No backup was found for '{itemId}'.");
        }

        try
        {
            await _backupService.RestoreBackupAsync(state.BackupFilePath, cancellationToken);
            _backupService.DeleteBackupFile(state.BackupFilePath);

            state.IsDeleted = false;
            state.BackupFilePath = null;
            state.DeletedAtUtc = null;
            state.IsPendingApproval = false;
            state.SuppressNextDetection = true;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            state.DesiredEnabled = null;
            PruneTransientStates(states);
            await _stateStore.SaveAsync(states, cancellationToken);

            await _logger.LogAsync($"Restored deleted item {state.DisplayName}.", cancellationToken);

            var refreshed = (await GetSnapshotAsync(cancellationToken))
                .FirstOrDefault(entry => string.Equals(entry.Id, itemId, StringComparison.OrdinalIgnoreCase));

            return new PipeResponse
            {
                Success = refreshed is not null,
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

    /// <summary>
    /// Executes mark Item Pending Approval Async.
    /// </summary>
    public async Task MarkItemPendingApprovalAsync(ContextMenuEntry item, CancellationToken cancellationToken)
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
    public async Task<ContextMenuEntry> QuarantineNewItemAsync(ContextMenuEntry item, CancellationToken cancellationToken)
    {
        // Step 1: disable the newly detected item immediately. This keeps the
        // service in a deny-by-default posture until the user explicitly allows it.
        switch (item.EntryKind)
        {
            case ContextMenuEntryKind.ShellVerb when !item.IsWindows11ContextMenu:
                SetShellVerbEnabled(item.BackendRegistryPath, enable: false);
                break;
            case ContextMenuEntryKind.ShellExtension when item.IsWindows11ContextMenu:
                if (!_windows11Catalog.SetEnabled(item.HandlerClsid ?? item.KeyName, item.DisplayName, null, enable: false))
                {
                    throw new InvalidOperationException($"Unable to quarantine the Win11 context menu item '{item.DisplayName}'.");
                }
                break;
            case ContextMenuEntryKind.ShellExtension:
                SetShellExtensionEnabled(item, enable: false);
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
        state.IsDeleted = false;
        state.DeletedAtUtc = null;
        state.BackupFilePath = null;
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _stateStore.SaveAsync(states, cancellationToken);

        await _logger.LogAsync($"Quarantined new menu item pending approval: {item.DisplayName} ({item.RegistryPath}).", cancellationToken);

        return (await GetSnapshotAsync(cancellationToken))
            .FirstOrDefault(entry => string.Equals(entry.Id, item.Id, StringComparison.OrdinalIgnoreCase))
            ?? item with
            {
                IsEnabled = false,
                IsPendingApproval = true
            };
    }

    /// <summary>
    /// Executes log Consistency Summary Async.
    /// </summary>
    public async Task<int> LogConsistencySummaryAsync(CancellationToken cancellationToken)
    {
        var inconsistencies = (await GetSnapshotAsync(cancellationToken)).Count(static entry => entry.HasConsistencyIssue);
        await _logger.LogAsync($"Consistency check complete. Inconsistent items: {inconsistencies}.", cancellationToken);
        return inconsistencies;
    }

    /// <summary>
    /// Attempts to consume Suppressed Detection Async.
    /// </summary>
    public async Task<bool> TryConsumeSuppressedDetectionAsync(string itemId, CancellationToken cancellationToken)
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

    private async Task<IReadOnlyList<ContextMenuEntry>> EnumerateActualEntriesAsync(CancellationToken cancellationToken)
    {
        var results = new List<ContextMenuEntry>();
        foreach (var item in EnumerateEntries(MonitoredRoots))
        {
            results.Add(item);
        }

        if (_windows11Catalog.IsSupported)
        {
            results.AddRange(await _windows11Catalog.EnumerateEntriesAsync(cancellationToken));
        }

        return results;
    }

    private IEnumerable<ContextMenuEntry> EnumerateEntries(IEnumerable<RegistryRootDescriptor> roots)
    {
        foreach (var root in roots)
        {
            foreach (var item in EnumerateRoot(root))
            {
                yield return item;
            }
        }
    }

    private IEnumerable<ContextMenuEntry> EnumerateRoot(RegistryRootDescriptor root)
    {
        foreach (var instance in EnumerateRootInstances())
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

                var effectiveRelativePath = $@"{root.StableRelativePath}\{subKeyName}";
                var isEnabled = root.EntryKind switch
                {
                    ContextMenuEntryKind.ShellVerb => itemKey.GetValue("LegacyDisable") is null,
                    ContextMenuEntryKind.ShellExtension => !IsShellExtensionBlocked(handlerClsid),
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
            ? ShellMetadataResolver.ResolveShellExtensionDisplayName(fallbackKeyName, handlerClsid)
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

    private static string? GetConsistencyIssue(ContextMenuEntry entry, PersistedContextMenuState? state)
    {
        if (state is null)
        {
            return null;
        }

        if (state.IsDeleted)
        {
            return "This item was deleted through the app, but it has reappeared in the registry.";
        }

        if (state.DesiredEnabled is { } desiredEnabled && entry.IsEnabled != desiredEnabled)
        {
            return $"Saved state expects this item to be {(desiredEnabled ? "enabled" : "disabled")}, but the registry currently reports {(entry.IsEnabled ? "enabled" : "disabled")}.";
        }

        return null;
    }

    private static ContextMenuChangeKind GetDetectedChangeKind(ContextMenuEntry entry, PersistedContextMenuState? state, bool hasBaseline)
    {
        if (state is null)
        {
            return hasBaseline ? ContextMenuChangeKind.Added : ContextMenuChangeKind.None;
        }

        if (state.IsDeleted)
        {
            return ContextMenuChangeKind.Reappeared;
        }

        return HasObservedChange(entry, state)
            ? ContextMenuChangeKind.Modified
            : ContextMenuChangeKind.None;
    }

    private static string? GetDetectedChangeDetails(ContextMenuEntry entry, PersistedContextMenuState? state, ContextMenuChangeKind changeKind)
    {
        return changeKind switch
        {
            ContextMenuChangeKind.Added => "This item is new compared with the last saved context menu snapshot.",
            ContextMenuChangeKind.Reappeared => "This item was previously deleted through the app, but it has reappeared in the registry.",
            ContextMenuChangeKind.Modified when state is not null => BuildModifiedDetails(entry, state),
            _ => null
        };
    }

    private static string? GetDeletedConsistencyIssue(PersistedContextMenuState state)
    {
        if (string.IsNullOrWhiteSpace(state.BackupFilePath) || !File.Exists(state.BackupFilePath))
        {
            return "The backup file for this deleted item is missing.";
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

    private static IReadOnlyList<ContextMenuEntry> GetStateLinkedEntries(
        IReadOnlyList<ContextMenuEntry> snapshot,
        ContextMenuEntry item)
    {
        if (item.EntryKind != ContextMenuEntryKind.ShellExtension || string.IsNullOrWhiteSpace(item.HandlerClsid))
        {
            return [item];
        }

        return snapshot
            .Where(entry =>
                entry.EntryKind == ContextMenuEntryKind.ShellExtension
                && string.Equals(entry.HandlerClsid, item.HandlerClsid, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static void UpdateLinkedShellExtensionPersistedStates(
        IDictionary<string, PersistedContextMenuState> states,
        string? handlerClsid,
        bool enable)
    {
        if (string.IsNullOrWhiteSpace(handlerClsid))
        {
            return;
        }

        foreach (var state in states.Values.Where(state =>
                     state.EntryKind == ContextMenuEntryKind.ShellExtension
                     && !state.IsDeleted
                     && string.Equals(state.HandlerClsid, handlerClsid, StringComparison.OrdinalIgnoreCase)))
        {
            state.DesiredEnabled = enable;
            state.ObservedEnabled = enable;
            state.IsPendingApproval = false;
            state.SuppressNextDetection = false;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
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

        return !state.IsDeleted
               && !state.IsPendingApproval
               && !state.SuppressNextDetection
               && state.DesiredEnabled is null
               && string.IsNullOrWhiteSpace(state.BackupFilePath);
    }

    private static bool HasObservedChange(ContextMenuEntry entry, PersistedContextMenuState state)
    {
        return HasExternalEnabledStateChange(entry, state)
               || state.Category != entry.Category
               || !string.Equals(state.DisplayName, entry.DisplayName, StringComparison.Ordinal)
               || !string.Equals(state.EditableText, entry.EditableText, StringComparison.Ordinal)
               || !string.Equals(state.CommandText, entry.CommandText, StringComparison.Ordinal)
               || !string.Equals(state.HandlerClsid, entry.HandlerClsid, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(state.IconPath, entry.IconPath, StringComparison.OrdinalIgnoreCase)
               || state.IconIndex != entry.IconIndex
               || !string.Equals(state.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase)
               || state.IsWindows11ContextMenu != entry.IsWindows11ContextMenu
               || state.OnlyWithShift != entry.OnlyWithShift
               || state.OnlyInExplorer != entry.OnlyInExplorer
               || state.NoWorkingDirectory != entry.NoWorkingDirectory
               || state.NeverDefault != entry.NeverDefault
               || state.ShowAsDisabledIfHidden != entry.ShowAsDisabledIfHidden
               || !string.Equals(state.Notes, entry.Notes, StringComparison.Ordinal)
               || !string.Equals(state.SourceRootPath, entry.SourceRootPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildModifiedDetails(ContextMenuEntry entry, PersistedContextMenuState state)
    {
        var changedParts = new List<string>();
        if (HasExternalEnabledStateChange(entry, state))
        {
            changedParts.Add("enabled state");
        }

        if (!string.Equals(state.DisplayName, entry.DisplayName, StringComparison.Ordinal))
        {
            changedParts.Add("display name");
        }

        if (!string.Equals(state.EditableText, entry.EditableText, StringComparison.Ordinal))
        {
            changedParts.Add("menu text");
        }

        if (!string.Equals(state.CommandText, entry.CommandText, StringComparison.Ordinal))
        {
            changedParts.Add("command");
        }

        if (!string.Equals(state.HandlerClsid, entry.HandlerClsid, StringComparison.OrdinalIgnoreCase))
        {
            changedParts.Add("handler GUID");
        }

        if (!string.Equals(state.IconPath, entry.IconPath, StringComparison.OrdinalIgnoreCase)
            || state.IconIndex != entry.IconIndex)
        {
            changedParts.Add("icon");
        }

        if (!string.Equals(state.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            changedParts.Add("module path");
        }

        if (state.OnlyWithShift != entry.OnlyWithShift)
        {
            changedParts.Add("extended visibility");
        }

        if (state.OnlyInExplorer != entry.OnlyInExplorer)
        {
            changedParts.Add("explorer-only flag");
        }

        if (state.NoWorkingDirectory != entry.NoWorkingDirectory)
        {
            changedParts.Add("working directory flag");
        }

        if (state.NeverDefault != entry.NeverDefault)
        {
            changedParts.Add("default action flag");
        }

        if (state.ShowAsDisabledIfHidden != entry.ShowAsDisabledIfHidden)
        {
            changedParts.Add("show-disabled-when-hidden flag");
        }

        if (!string.Equals(state.Notes, entry.Notes, StringComparison.Ordinal))
        {
            changedParts.Add("details");
        }

        if (!string.Equals(state.SourceRootPath, entry.SourceRootPath, StringComparison.OrdinalIgnoreCase)
            || state.Category != entry.Category)
        {
            changedParts.Add("category");
        }

        return changedParts.Count == 0
            ? "This item changed outside the app."
            : $"This item changed outside the app. Updated fields: {string.Join(", ", changedParts)}.";
    }

    private static bool HasExternalEnabledStateChange(ContextMenuEntry entry, PersistedContextMenuState state)
    {
        if (state.DesiredEnabled is { } desiredEnabled)
        {
            return entry.IsEnabled != desiredEnabled;
        }

        return state.ObservedEnabled != entry.IsEnabled;
    }

    private static bool IsShellExtensionBlocked(string? handlerClsid)
    {
        if (string.IsNullOrWhiteSpace(handlerClsid))
        {
            return false;
        }

        using var blockedKey = Registry.LocalMachine.OpenSubKey(BlockedShellExtensionsPath, writable: false);
        return blockedKey?.GetValue(handlerClsid) is not null;
    }

    private static void SetShellVerbEnabled(string registryPath, bool enable)
    {
        using var menuKey = OpenRegistryKey(registryPath, writable: true)
            ?? throw new InvalidOperationException($"Unable to open {registryPath} for writing.");

        if (enable)
        {
            menuKey.DeleteValue("LegacyDisable", throwOnMissingValue: false);
        }
        else
        {
            menuKey.SetValue("LegacyDisable", string.Empty, RegistryValueKind.String);
        }
    }

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

    private static void SetShellExtensionEnabled(ContextMenuEntry item, bool enable)
    {
        if (string.IsNullOrWhiteSpace(item.HandlerClsid))
        {
            throw new InvalidOperationException("Shell extension entries require a CLSID to be enabled or disabled.");
        }

        using var blockedKey = Registry.LocalMachine.CreateSubKey(BlockedShellExtensionsPath, writable: true)
            ?? throw new InvalidOperationException($"Unable to open {BlockedShellExtensionsPath} for writing.");

        if (enable)
        {
            blockedKey.DeleteValue(item.HandlerClsid, throwOnMissingValue: false);
        }
        else
        {
            blockedKey.SetValue(item.HandlerClsid, item.DisplayName, RegistryValueKind.String);
        }
    }

    private static void DeleteRegistryKey(string registryPath)
    {
        DeleteRegistryKeyTree(registryPath);
    }

    private static void SetEnhanceShellItemEnabled(string relativeGroupPath, XElement itemElement, bool enable)
    {
        var keyName = itemElement.Attribute("KeyName")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(keyName))
        {
            throw new InvalidOperationException("Enhance shell items require a KeyName attribute.");
        }

        var registryPath = $@"{relativeGroupPath}\shell\{keyName}";
        if (enable)
        {
            WriteEnhanceSubKeysValue(itemElement, registryPath);
        }
        else
        {
            Registry.ClassesRoot.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
        }
    }

    private static void SetEnhanceShellExItemEnabled(string relativeGroupPath, XElement itemElement, bool enable)
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

        var handlersPath = $@"{relativeGroupPath}\shellex\ContextMenuHandlers";
        if (enable)
        {
            using var handlersKey = Registry.ClassesRoot.CreateSubKey(handlersPath, writable: true)
                ?? throw new InvalidOperationException($"Unable to open {handlersPath} for writing.");

            var targetPath = $@"{handlersPath}\{keyName}";
            if (Registry.ClassesRoot.OpenSubKey(targetPath, writable: false) is not null)
            {
                targetPath = GetUniqueRegistryPath(handlersPath, keyName);
            }

            Registry.SetValue($@"HKEY_CLASSES_ROOT\{targetPath}", string.Empty, guid.ToString("B"), RegistryValueKind.String);
        }
        else
        {
            using var handlersKey = Registry.ClassesRoot.OpenSubKey(handlersPath, writable: true);
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
                    handlersKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                }
            }
        }
    }

    private static void WriteEnhanceSubKeysValue(XElement keyElement, string registryPath)
    {
        if (!ShouldIncludeNode(keyElement))
        {
            return;
        }

        var defaultValue = keyElement.Attribute("Default")?.Value;
        if (!string.IsNullOrWhiteSpace(defaultValue))
        {
            Registry.SetValue($@"HKEY_CLASSES_ROOT\{registryPath}", string.Empty, Environment.ExpandEnvironmentVariables(defaultValue));
        }
        else if (string.Equals(keyElement.Name.LocalName, "Command", StringComparison.OrdinalIgnoreCase))
        {
            WriteEnhanceCommandValue(keyElement, registryPath);
        }

        WriteEnhanceAttributesValue(keyElement.Element("Value"), registryPath);

        var subKeyElement = keyElement.Element("SubKey");
        if (subKeyElement is null)
        {
            return;
        }

        foreach (var childElement in subKeyElement.Elements())
        {
            WriteEnhanceSubKeysValue(childElement, $@"{registryPath}\{childElement.Name.LocalName}");
        }
    }

    private static void WriteEnhanceAttributesValue(XElement? valueElement, string registryPath)
    {
        if (valueElement is null || !ShouldIncludeNode(valueElement))
        {
            return;
        }

        using var key = Registry.ClassesRoot.CreateSubKey(registryPath, writable: true)
            ?? throw new InvalidOperationException($"Unable to open {registryPath} for writing.");

        foreach (var valueNode in valueElement.Elements().Where(ShouldIncludeNode))
        {
            foreach (var attribute in valueNode.Attributes())
            {
                var attributeValue = attribute.Value;
                switch (valueNode.Name.LocalName)
                {
                    case "REG_SZ":
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
                        key.SetValue(attribute.Name.LocalName, Convert.ToInt32(attributeValue, numericBase), RegistryValueKind.DWord);
                        break;
                }
            }
        }
    }

    private static void WriteEnhanceCommandValue(XElement commandElement, string registryPath)
    {
        var fileNameElement = commandElement.Element("FileName");
        var argumentsElement = commandElement.Element("Arguments");
        var shellExecuteElement = commandElement.Element("ShellExecute");

        var fileName = fileNameElement?.Value?.Trim();
        var arguments = argumentsElement?.Value?.Trim();

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = CreateEnhanceCommandFile(fileNameElement);
        }

        if (string.IsNullOrWhiteSpace(arguments))
        {
            arguments = CreateEnhanceCommandFile(argumentsElement);
        }

        fileName = Environment.ExpandEnvironmentVariables(fileName ?? string.Empty);
        arguments = Environment.ExpandEnvironmentVariables(arguments ?? string.Empty);
        arguments = $"{argumentsElement?.Attribute("Prefix")?.Value}{arguments}{argumentsElement?.Attribute("Suffix")?.Value}";

        string command;
        if (shellExecuteElement is not null)
        {
            var verb = shellExecuteElement.Attribute("Verb")?.Value ?? "open";
            var windowStyle = int.TryParse(shellExecuteElement.Attribute("WindowStyle")?.Value, out var parsedStyle) ? parsedStyle : 1;
            var directory = shellExecuteElement.Attribute("Directory")?.Value;
            command = BuildShellExecuteCommand(fileName, arguments, verb, windowStyle, directory);
        }
        else
        {
            command = fileName;
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                command += $" {arguments}";
            }
        }

        Registry.SetValue($@"HKEY_CLASSES_ROOT\{registryPath}", string.Empty, command, RegistryValueKind.String);
    }

    private static string CreateEnhanceCommandFile(XElement? parentElement)
    {
        if (parentElement is null)
        {
            return string.Empty;
        }

        var generatedDir = Path.Combine(
            RuntimePaths.RootDirectory,
            "Generated");
        Directory.CreateDirectory(generatedDir);

        foreach (var createFileElement in parentElement.Elements("CreateFile").Where(ShouldIncludeNode))
        {
            var fileName = createFileElement.Attribute("FileName")?.Value;
            var content = createFileElement.Attribute("Content")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var filePath = Path.Combine(generatedDir, fileName);
            var encoding = string.Equals(Path.GetExtension(fileName), ".bat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(fileName), ".cmd", StringComparison.OrdinalIgnoreCase)
                    ? Encoding.Default
                    : Encoding.Unicode;

            File.WriteAllText(filePath, content, encoding);
            return filePath;
        }

        return string.Empty;
    }

    private static string BuildShellExecuteCommand(
        string fileName,
        string arguments,
        string verb,
        int windowStyle,
        string? directory)
    {
        arguments = arguments.Replace("\"", "\"\"");
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.GetDirectoryName(ExtractExecutablePath(fileName))
            : Environment.ExpandEnvironmentVariables(directory);

        return "mshta vbscript:createobject(\"shell.application\").shellexecute"
            + $"(\"{fileName}\",\"{arguments}\",\"{directory}\",\"{verb}\",{windowStyle})(close)";
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
        return value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static part => Convert.ToByte(part, 16))
            .ToArray();
    }

    private static bool ShouldIncludeNode(XElement element)
    {
        if (!HasRequiredFiles(element))
        {
            return false;
        }

        if (!MatchesOsVersion(element))
        {
            return false;
        }

        return MatchesCulture(element);
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

    private static bool MatchesCulture(XElement element)
    {
        var culture = element.Element("Culture")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(culture))
        {
            return true;
        }

        return string.Equals(culture, CultureInfo.CurrentUICulture.Name, StringComparison.OrdinalIgnoreCase);
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

    private static PipeResponse CreateFailure(string message, ContextMenuEntry? item = null)
    {
        return new PipeResponse
        {
            Success = false,
            Message = message,
            Item = item
        };
    }

    private static List<string> ApplyRegistryWriteProtection(bool enable)
    {
        var errors = new List<string>();

        foreach (var relativePath in MonitoredRoots
                     .Select(static root => root.StableRelativePath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ApplyRegistryWriteProtection(RegistryHive.LocalMachine, relativePath, enable, errors);
            ApplyRegistryWriteProtection(RegistryHive.CurrentUser, relativePath, enable, errors);
        }

        return errors;
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
    }

    private static IEnumerable<RegistryRootDescriptor> GetSceneRoots(ContextMenuSceneKind sceneKind, string? scopeValue)
    {
        return sceneKind switch
        {
            ContextMenuSceneKind.LnkFile => CreateShellSceneRoots(ContextMenuCategory.File, "lnkfile"),
            ContextMenuSceneKind.UwpShortcut => CreateShellSceneRoots(ContextMenuCategory.File, "Launcher.ImmersiveApplication"),
            ContextMenuSceneKind.ExeFile => CreateShellSceneRoots(ContextMenuCategory.File, "exefile"),
            ContextMenuSceneKind.UnknownType => CreateShellSceneRoots(ContextMenuCategory.File, "Unknown"),
            ContextMenuSceneKind.CustomExtension => CreateCustomExtensionRoots(scopeValue),
            ContextMenuSceneKind.PerceivedType => CreatePerceivedTypeRoots(scopeValue),
            ContextMenuSceneKind.DirectoryType => CreateDirectoryTypeRoots(scopeValue),
            ContextMenuSceneKind.CustomRegistryPath => CreateCustomRegistryPathRoots(scopeValue),
            _ => []
        };
    }

    private static IEnumerable<RegistryRootDescriptor> CreateShellSceneRoots(ContextMenuCategory category, string basePath)
    {
        yield return new RegistryRootDescriptor(category, $@"{basePath}\shell", ContextMenuEntryKind.ShellVerb);
        yield return new RegistryRootDescriptor(category, $@"{basePath}\shellex\ContextMenuHandlers", ContextMenuEntryKind.ShellExtension);
        yield return new RegistryRootDescriptor(
            category,
            $@"{basePath}\shellex\-ContextMenuHandlers",
            ContextMenuEntryKind.ShellExtension,
            $@"{basePath}\shellex\ContextMenuHandlers",
            true);
    }

    private static IEnumerable<RegistryRootDescriptor> CreateCustomExtensionRoots(string? scopeValue)
    {
        var extension = NormalizeExtension(scopeValue);
        if (string.IsNullOrWhiteSpace(extension))
        {
            yield break;
        }

        foreach (var root in CreateShellSceneRoots(ContextMenuCategory.File, $@"SystemFileAssociations\{extension}"))
        {
            yield return root;
        }

        using var extensionKey = Registry.ClassesRoot.OpenSubKey(extension, writable: false);
        var progId = extensionKey?.GetValue(null)?.ToString();
        if (string.IsNullOrWhiteSpace(progId))
        {
            yield break;
        }

        foreach (var root in CreateShellSceneRoots(ContextMenuCategory.File, progId))
        {
            yield return new RegistryRootDescriptor(
                root.Category,
                root.RelativePath,
                root.EntryKind,
                $@"SystemFileAssociations\{extension}",
                root.IsDisabledContainer);
        }
    }

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

    private static ContextMenuEntry SelectPreferredActualEntry(ContextMenuEntry existing, ContextMenuEntry candidate)
    {
        var existingIsDisabledContainer = IsDisabledContainerEntry(existing);
        var candidateIsDisabledContainer = IsDisabledContainerEntry(candidate);

        if (existingIsDisabledContainer == candidateIsDisabledContainer)
        {
            return candidate;
        }

        return existingIsDisabledContainer ? candidate : existing;
    }

    private static bool IsDisabledContainerEntry(ContextMenuEntry entry)
    {
        return entry.RegistryPath.Contains(@"\-ContextMenuHandlers\", StringComparison.OrdinalIgnoreCase)
               || entry.BackendRegistryPath.Contains(@"\-ContextMenuHandlers\", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<RegistryRootInstance> EnumerateRootInstances()
    {
        yield return new RegistryRootInstance(
            Registry.LocalMachine,
            @"SOFTWARE\Classes",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes");

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

    private static RegistryKey? OpenRegistryKey(string absoluteRegistryPath, bool writable)
    {
        if (TrySplitAbsoluteRegistryPath(absoluteRegistryPath, out var rootKey, out var subPath))
        {
            return rootKey.OpenSubKey(subPath, writable);
        }

        return Registry.ClassesRoot.OpenSubKey(absoluteRegistryPath, writable);
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
        bool IsDisabledContainer = false)
    {
        /// <summary>
        /// Gets the stable Relative Path.
        /// </summary>
        public string StableRelativePath { get; } = StableRelativePath ?? RelativePath;
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
}
