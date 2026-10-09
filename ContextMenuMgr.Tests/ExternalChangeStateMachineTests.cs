using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Xunit;

namespace ContextMenuMgr.Tests;

/// <summary>
/// Focused tests for the external context-menu change state machine.
///
/// These tests exercise the pure, deterministic classifier helpers extracted from
/// <see cref="ContextMenuRegistryCatalog"/>. They never touch the real registry or
/// the state store, so they run quickly and deterministically in any environment.
///
/// The classifier (<see cref="ContextMenuChangeClassifier.ClassifyItemMonitorAction"/>
/// and its helpers) is the single source of truth for the state-machine matrix
/// documented in docs/registry-model.md.
/// </summary>
public sealed class ExternalChangeStateMachineTests
{
    [Fact]
    public void ClassicShellExtensionPaths_UseStableIdentityAndPerRegistrationContainers()
    {
        const string activePath = @"HKEY_USERS\S-1-5-21-test\Software\Classes\*\shellex\ContextMenuHandlers\Foo";
        const string disabledPath = @"HKEY_USERS\S-1-5-21-test\Software\Classes\*\shellex\-ContextMenuHandlers\Foo";

        Assert.False(ContextMenuRegistryCatalog.IsDisabledContextMenuHandlersPath(activePath));
        Assert.True(ContextMenuRegistryCatalog.IsDisabledContextMenuHandlersPath(disabledPath));
        Assert.Equal(disabledPath, ContextMenuRegistryCatalog.GetSiblingContextMenuHandlersPath(activePath, enable: false));
        Assert.Equal(activePath, ContextMenuRegistryCatalog.GetSiblingContextMenuHandlersPath(disabledPath, enable: true));

        var active = new ContextMenuEntry
        {
            Id = @"*\shellex\ContextMenuHandlers|Foo",
            EntryKind = ContextMenuEntryKind.ShellExtension,
            IsEnabled = true
        };
        var disabled = active with { IsEnabled = false };

        Assert.Equal(active.Id, disabled.Id);
        Assert.True(active.IsEnabled);
        Assert.False(disabled.IsEnabled);
    }

    [Fact]
    public void ClassicShellExtensionsWithSameClsid_HaveIndependentStateControlDomains()
    {
        var file = new ContextMenuEntry
        {
            Id = @"*\shellex\ContextMenuHandlers|Foo",
            EntryKind = ContextMenuEntryKind.ShellExtension,
            HandlerClsid = "{11111111-1111-1111-1111-111111111111}"
        };
        var directory = file with { Id = @"Directory\shellex\ContextMenuHandlers|Foo" };

        var linked = ContextMenuRegistryCatalog.GetStateLinkedEntries([file, directory], file);

        Assert.Single(linked);
        Assert.Equal(file.Id, linked[0].Id);
    }

    [Fact]
    public void Windows11EntriesWithSameClsid_RetainGlobalBlockedControlDomain()
    {
        var first = new ContextMenuEntry
        {
            Id = "win11:first",
            EntryKind = ContextMenuEntryKind.ShellExtension,
            IsWindows11ContextMenu = true,
            HandlerClsid = "{22222222-2222-2222-2222-222222222222}"
        };
        var second = first with { Id = "win11:second" };

        var linked = ContextMenuRegistryCatalog.GetStateLinkedEntries([first, second], first);

        Assert.Equal(2, linked.Count);
        Assert.Contains(linked, item => item.Id == first.Id);
        Assert.Contains(linked, item => item.Id == second.Id);
    }

    private static ContextMenuEntry BuildPresentEntry(
        bool isEnabled = true,
        string displayName = "Test Verb",
        string commandText = "\"C:\\App\\app.exe\" \"%1\"",
        string? handlerClsid = null,
        ContextMenuEntryKind entryKind = ContextMenuEntryKind.ShellVerb,
        string id = "shell:HKEY_CLASSES_ROOT\\*\\shell\\testverb")
        => new()
        {
            Id = id,
            Category = ContextMenuCategory.File,
            EntryKind = entryKind,
            KeyName = "testverb",
            DisplayName = displayName,
            EditableText = displayName,
            RegistryPath = "HKEY_CLASSES_ROOT\\*\\shell\\testverb",
            BackendRegistryPath = "HKEY_CLASSES_ROOT\\*\\shell\\testverb",
            SourceRootPath = "HKEY_CLASSES_ROOT\\*\\shell",
            CommandText = commandText,
            HandlerClsid = handlerClsid,
            IsPresentInRegistry = true,
            IsEnabled = isEnabled,
            DetectedChangeKind = ContextMenuChangeKind.None
        };

    private static PersistedContextMenuState BuildState(
        bool isDeleted = false,
        bool? desiredEnabled = null,
        bool observedEnabled = true,
        bool isPendingApproval = false,
        ContextMenuChangeKind? pendingApprovalChangeKind = null,
        string? backupFilePath = null,
        DateTimeOffset? deletedAtUtc = null,
        string displayName = "Test Verb",
        string commandText = "\"C:\\App\\app.exe\" \"%1\"",
        string? handlerClsid = null)
        => new()
        {
            Id = "shell:HKEY_CLASSES_ROOT\\*\\shell\\testverb",
            Category = ContextMenuCategory.File,
            EntryKind = ContextMenuEntryKind.ShellVerb,
            KeyName = "testverb",
            DisplayName = displayName,
            EditableText = displayName,
            RegistryPath = "HKEY_CLASSES_ROOT\\*\\shell\\testverb",
            BackendRegistryPath = "HKEY_CLASSES_ROOT\\*\\shell\\testverb",
            SourceRootPath = "HKEY_CLASSES_ROOT\\*\\shell",
            CommandText = commandText,
            HandlerClsid = handlerClsid,
            ObservedEnabled = observedEnabled,
            DesiredEnabled = desiredEnabled,
            IsDeleted = isDeleted,
            IsPendingApproval = isPendingApproval,
            PendingApprovalChangeKind = pendingApprovalChangeKind,
            BackupFilePath = backupFilePath,
            DeletedAtUtc = deletedAtUtc
        };

    /// <summary>
    /// Scenario 1: On the first-ever run with an empty persisted state database,
    /// existing items must be adopted as the initial baseline. No quarantine,
    /// no highlights, no approval notifications caused solely by first run.
    /// </summary>
    [Fact]
    public void FirstRun_EmptyState_AdoptsAsBaseline_NoQuarantine()
    {
        var entry = BuildPresentEntry(isEnabled: true);
        PersistedContextMenuState? state = null;

        // No baseline exists yet (first run).
        const bool hasBaseline = false;
        // Startup/offline context.
        const bool isBaselineEstablishment = true;

        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline, isBaselineEstablishment);

        Assert.Equal(ItemMonitorAction.None, action);

        // Detected change kind must also be None (not Added) so the frontend
        // does not highlight every pre-existing item on first run.
        var changeKind = ContextMenuChangeClassifier.GetDetectedChangeKind(entry, state, hasBaseline);
        Assert.Equal(ContextMenuChangeKind.None, changeKind);

        // No consistency issue on first run.
        Assert.Null(ContextMenuChangeClassifier.GetConsistencyIssue(entry, state));
    }

    /// <summary>
    /// Scenario 9: When DesiredEnabled=true (user explicitly enabled the item)
    /// but the actual registry reports it as disabled, the classifier must NOT
    /// trigger automatic enable. Only DesiredEnabled=false is continuously
    /// enforced; DesiredEnabled=true is a recorded preference, not an
    /// enforced policy.
    /// </summary>
    [Fact]
    public void DesiredEnabledTrue_ActualDisabled_NoAutomaticEnable()
    {
        var entry = BuildPresentEntry(isEnabled: false);
        var state = BuildState(
            isDeleted: false,
            desiredEnabled: true,
            observedEnabled: true);

        // Must not reconcile: DesiredEnabled=true is not enforced.
        Assert.False(ContextMenuChangeClassifier.ShouldReconcileDisabledState(entry, state));

        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline: true, isBaselineEstablishment: false);

        // The enabled-state drift is observed as an external modification, but
        // it is not duplicated as a generic consistency warning.
        Assert.Equal(ItemMonitorAction.MetadataModifiedHighlight, action);

        Assert.Null(ContextMenuChangeClassifier.GetConsistencyIssue(entry, state));
    }

    /// <summary>
    /// Scenario 10: When a known item (state exists, not deleted, not pending
    /// approval, no enabled-state drift) changes only metadata (e.g. display
    /// name, command text, icon), the classifier must return
    /// MetadataModifiedHighlight. No automatic rollback, no quarantine.
    /// </summary>
    [Fact]
    public void KnownMetadataOnlyChange_ModifiedHighlight_NoQuarantine()
    {
        var entry = BuildPresentEntry(
            isEnabled: true,
            displayName: "Renamed Verb",
            commandText: "\"C:\\App\\app-v2.exe\" \"%1\"");

        var state = BuildState(
            isDeleted: false,
            desiredEnabled: null,
            observedEnabled: true,
            displayName: "Original Verb",
            commandText: "\"C:\\App\\app.exe\" \"%1\"");

        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline: true, isBaselineEstablishment: false);

        Assert.Equal(ItemMonitorAction.MetadataModifiedHighlight, action);

        var changeKind = ContextMenuChangeClassifier.GetDetectedChangeKind(entry, state, hasBaseline: true);
        Assert.Equal(ContextMenuChangeKind.Modified, changeKind);

        var details = ContextMenuChangeClassifier.GetDetectedChangeDetails(entry, state, changeKind);
        Assert.NotNull(details);
        Assert.Contains("display name", details!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("command", details!, StringComparison.OrdinalIgnoreCase);

        // No enabled-state drift, so no consistency issue from that.
        Assert.False(ContextMenuChangeClassifier.HasExternalEnabledStateChange(entry, state));
    }

    /// <summary>
    /// When an item is already pending approval, the classifier must return
    /// None so the monitor does not re-quarantine or re-notify on every poll.
    /// This prevents approval-notification spam when a third-party app
    /// repeatedly recreates the same item.
    /// </summary>
    [Fact]
    public void AlreadyPendingApproval_DoesNotReQuarantine()
    {
        var entry = BuildPresentEntry(isEnabled: true);
        var state = BuildState(
            isDeleted: true,
            desiredEnabled: null,
            observedEnabled: false,
            isPendingApproval: true,
            pendingApprovalChangeKind: ContextMenuChangeKind.Reappeared,
            backupFilePath: "C:\\Backups\\old.reg",
            deletedAtUtc: DateTimeOffset.UtcNow.AddDays(-1));

        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline: true, isBaselineEstablishment: false);

        Assert.Equal(ItemMonitorAction.None, action);
    }

    /// <summary>
    /// When an explicit-disabled item is also marked pending approval, the
    /// pending-approval state takes precedence: no reconciliation. This
    /// prevents the monitor from fighting an in-flight approval operation.
    /// </summary>
    [Fact]
    public void ExplicitDisabledButPendingApproval_NoReconciliation()
    {
        var entry = BuildPresentEntry(isEnabled: true);
        var state = BuildState(
            isDeleted: false,
            desiredEnabled: false,
            observedEnabled: false,
            isPendingApproval: true,
            pendingApprovalChangeKind: ContextMenuChangeKind.Added);

        Assert.False(ContextMenuChangeClassifier.ShouldReconcileDisabledState(entry, state));

        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline: true, isBaselineEstablishment: false);
        Assert.Equal(ItemMonitorAction.None, action);
    }

    /// <summary>
    /// Verifies that HasObservedChange detects a CLSID change, which is
    /// important for ShellExtension identity continuity.
    /// </summary>
    [Fact]
    public void HasObservedChange_DetectsClsidChange()
    {
        var entry = BuildPresentEntry(handlerClsid: "{NEW-CLSID-1234}");
        var state = BuildState(handlerClsid: "{OLD-CLSID-5678}");

        Assert.True(ContextMenuChangeClassifier.HasObservedChange(entry, state));
    }

    /// <summary>
    /// Verifies that an entry matching its persisted state exactly (same
    /// enabled state, same metadata, DesiredEnabled=null) produces no
    /// observed change and no action.
    /// </summary>
    [Fact]
    public void MatchingState_NoObservedChange_NoAction()
    {
        var entry = BuildPresentEntry(isEnabled: true);
        var state = BuildState(
            isDeleted: false,
            desiredEnabled: null,
            observedEnabled: true);

        Assert.False(ContextMenuChangeClassifier.HasObservedChange(entry, state));
        Assert.False(ContextMenuChangeClassifier.HasExternalEnabledStateChange(entry, state));

        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline: true, isBaselineEstablishment: false);
        Assert.Equal(ItemMonitorAction.None, action);

        Assert.Null(ContextMenuChangeClassifier.GetConsistencyIssue(entry, state));
    }

    /// <summary>
    /// Verifies the full classification matrix for cases where persisted state
    /// exists, so the runtime-vs-startup behavior difference is auditable at
    /// a glance. The null-state cases (QuarantineAdded / OfflineAddedHighlight)
    /// are covered by ContextMenuMonitoringRulesTests.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, true, false, ItemMonitorAction.QuarantineAdded)]
    [InlineData(true, false, false, true, true, ItemMonitorAction.OfflineAddedHighlight)]
    [InlineData(false, true, false, true, false, ItemMonitorAction.ReconcileDisabledState)]
    [InlineData(false, true, false, true, true, ItemMonitorAction.MetadataModifiedHighlight)]
    public void ClassificationMatrix_FullCoverage(
        bool stateIsDeleted,
        bool stateDesiredDisabled,
        bool statePendingApproval,
        bool hasBaseline,
        bool isBaselineEstablishment,
        ItemMonitorAction expectedAction)
    {
        var entry = BuildPresentEntry(isEnabled: true);
        var state = BuildState(
            isDeleted: stateIsDeleted,
            desiredEnabled: stateDesiredDisabled ? false : null,
            observedEnabled: !stateDesiredDisabled,
            isPendingApproval: statePendingApproval,
            backupFilePath: stateIsDeleted ? "C:\\Backups\\old.reg" : null,
            deletedAtUtc: stateIsDeleted ? DateTimeOffset.UtcNow.AddDays(-1) : null);

        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline, isBaselineEstablishment);

        Assert.Equal(expectedAction, action);
    }

    /// <summary>
    /// Deleted recovery state does not preserve monitoring identity.
    /// </summary>
    [Fact]
    public void GetDetectedChangeKind_DeletedStateFollowsOrdinaryBaselineRule()
    {
        var entry = BuildPresentEntry(isEnabled: true);
        var state = BuildState(
            isDeleted: true,
            observedEnabled: false,
            backupFilePath: "C:\\Backups\\old.reg",
            deletedAtUtc: DateTimeOffset.UtcNow.AddDays(-1));

        var withBaseline = ContextMenuChangeClassifier.GetDetectedChangeKind(entry, state, hasBaseline: true);
        Assert.Equal(ContextMenuChangeKind.Added, withBaseline);

        var withoutBaseline = ContextMenuChangeClassifier.GetDetectedChangeKind(entry, state, hasBaseline: false);
        Assert.Equal(ContextMenuChangeKind.None, withoutBaseline);
    }

    /// <summary>
    /// All active identities are removed after confirmed registry deletion;
    /// recovery-only deleted records remain available for Undo Delete.
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, null, true)]
    [InlineData(true, false, false)]
    [InlineData(true, null, false)]
    public void ShouldRemoveMissingState_Matrix(
        bool isDeleted, bool? desiredEnabled, bool expected)
    {
        var state = BuildState(
            isDeleted: isDeleted,
            desiredEnabled: desiredEnabled,
            observedEnabled: !desiredEnabled ?? true);

        Assert.Equal(expected, ContextMenuChangeClassifier.ShouldRemoveMissingState(state));
    }
}

/// <summary>
/// Tests for PersistedContextMenuState JSON migration and field consistency,
/// including compatibility with retired values written by older releases.
/// </summary>
public sealed class PersistedContextMenuStateTests
{
    /// <summary>
    /// A deleted record is recovery metadata only and must never remain in the
    /// pending-approval workflow, including when loaded from an old state file.
    /// </summary>
    [Fact]
    public void ToDeletedEntry_ClearsLegacyPendingApproval()
    {
        var state = new PersistedContextMenuState
        {
            Id = "test",
            IsDeleted = true,
            IsPendingApproval = true,
            PendingApprovalChangeKind = ContextMenuChangeKind.Reappeared,
            BackupFilePath = "C:\\Backups\\old.reg",
            DeletedAtUtc = DateTimeOffset.UtcNow.AddDays(-1)
        };

        var entry = state.ToDeletedEntry();

        Assert.True(entry.IsDeleted);
        Assert.False(entry.IsPendingApproval);
        Assert.True(entry.HasBackup);
        Assert.NotNull(entry.DeletedAtUtc);
    }
}
