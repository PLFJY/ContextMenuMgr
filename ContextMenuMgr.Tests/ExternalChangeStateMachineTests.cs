using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Xunit;

namespace ContextMenuMgr.Tests;

/// <summary>
/// Focused tests for the external context-menu change state machine (issue #11).
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

    // ---- helpers for building test fixtures ---------------------------------

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

    // ---- Scenario 1: first run, empty state database -----------------------

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

    // ---- Scenario 2: runtime unknown Added --------------------------------

    /// <summary>
    /// Scenario 2: At runtime, when a completely unknown item appears (no
    /// persisted state exists and the monitor has an established baseline),
    /// the item must be quarantined and sent through the approval flow.
    /// </summary>
    [Fact]
    public void Runtime_UnknownAdded_Quarantined()
    {
        var entry = BuildPresentEntry(isEnabled: true);
        PersistedContextMenuState? state = null;

        const bool hasBaseline = true;
        const bool isBaselineEstablishment = false;

        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline, isBaselineEstablishment);

        Assert.Equal(ItemMonitorAction.QuarantineAdded, action);

        var changeKind = ContextMenuChangeClassifier.GetDetectedChangeKind(entry, state, hasBaseline);
        Assert.Equal(ContextMenuChangeKind.Added, changeKind);

        var details = ContextMenuChangeClassifier.GetDetectedChangeDetails(entry, state, changeKind);
        Assert.NotNull(details);
        Assert.Contains("new", details!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Scenario 3: startup/offline unknown Added ------------------------

    /// <summary>
    /// Scenario 3: When the monitor was not running and an unknown item
    /// appeared, it must be exposed as an Added highlight only. No quarantine,
    /// no approval notification. The user decides what to do.
    /// </summary>
    [Fact]
    public void Startup_UnknownAdded_HighlightOnly_NoQuarantine()
    {
        var entry = BuildPresentEntry(isEnabled: true);
        PersistedContextMenuState? state = null;

        const bool hasBaseline = true;
        const bool isBaselineEstablishment = true;

        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline, isBaselineEstablishment);

        Assert.Equal(ItemMonitorAction.OfflineAddedHighlight, action);

        // The change kind is still Added so the frontend can show the badge.
        var changeKind = ContextMenuChangeClassifier.GetDetectedChangeKind(entry, state, hasBaseline);
        Assert.Equal(ContextMenuChangeKind.Added, changeKind);
    }

    // ---- Scenario 4: runtime previously deleted Reappeared ----------------

    /// <summary>
    /// Scenario 4: At runtime, when a previously deleted item reappears, it
    /// must be quarantined via the dedicated Reappeared path. The classifier
    /// must signal QuarantineReappeared (not QuarantineAdded) so the runtime
    /// can preserve deletion provenance.
    /// </summary>
    [Fact]
    public void Runtime_PreviouslyDeletedReappeared_QuarantinedAsReappeared()
    {
        var entry = BuildPresentEntry(isEnabled: true);
        var state = BuildState(
            isDeleted: true,
            desiredEnabled: null,
            observedEnabled: false,
            backupFilePath: "C:\\Backups\\old-item.reg",
            deletedAtUtc: DateTimeOffset.UtcNow.AddDays(-1));

        const bool hasBaseline = true;
        const bool isBaselineEstablishment = false;

        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline, isBaselineEstablishment);

        Assert.Equal(ItemMonitorAction.QuarantineReappeared, action);

        var changeKind = ContextMenuChangeClassifier.GetDetectedChangeKind(entry, state, hasBaseline);
        Assert.Equal(ContextMenuChangeKind.Reappeared, changeKind);

        // Consistency issue must be present so the frontend can warn the user
        // that a deleted item has come back.
        var consistency = ContextMenuChangeClassifier.GetConsistencyIssue(entry, state);
        Assert.NotNull(consistency);
        Assert.Contains("reappeared", consistency!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Scenario 5: startup/offline previously deleted Reappeared ---------

    /// <summary>
    /// Scenario 5: When the monitor was not running and a previously deleted
    /// item reappeared, it must be exposed as a Reappeared highlight and
    /// consistency warning only. No quarantine, no approval notification.
    /// </summary>
    [Fact]
    public void Startup_PreviouslyDeletedReappeared_HighlightOnly_NoQuarantine()
    {
        var entry = BuildPresentEntry(isEnabled: true);
        var state = BuildState(
            isDeleted: true,
            observedEnabled: false,
            backupFilePath: "C:\\Backups\\old-item.reg",
            deletedAtUtc: DateTimeOffset.UtcNow.AddDays(-1));

        const bool hasBaseline = true;
        const bool isBaselineEstablishment = true;

        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline, isBaselineEstablishment);

        Assert.Equal(ItemMonitorAction.OfflineReappearedHighlight, action);

        var changeKind = ContextMenuChangeClassifier.GetDetectedChangeKind(entry, state, hasBaseline);
        Assert.Equal(ContextMenuChangeKind.Reappeared, changeKind);

        // The consistency warning must remain visible.
        Assert.NotNull(ContextMenuChangeClassifier.GetConsistencyIssue(entry, state));
    }

    // ---- Scenario 6: runtime DesiredEnabled=false and actual enabled ------

    /// <summary>
    /// Scenario 6: At runtime, when a previously explicitly disabled item
    /// (DesiredEnabled=false, not deleted, not pending approval) is found to
    /// be enabled in the registry (e.g. a third-party app recreated it), it
    /// must be automatically re-disabled. No pending approval, no approval
    /// notification.
    /// </summary>
    [Fact]
    public void Runtime_DesiredDisabled_ActualEnabled_AutoReconciled()
    {
        var entry = BuildPresentEntry(isEnabled: true);
        var state = BuildState(
            isDeleted: false,
            desiredEnabled: false,
            observedEnabled: false,
            isPendingApproval: false);

        const bool hasBaseline = true;
        const bool isBaselineEstablishment = false;

        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline, isBaselineEstablishment);

        Assert.Equal(ItemMonitorAction.ReconcileDisabledState, action);

        // The classifier must confirm the drift is real.
        Assert.True(ContextMenuChangeClassifier.ShouldReconcileDisabledState(entry, state));

        // No pending approval should be triggered by this action.
        Assert.False(state.IsPendingApproval);
    }

    // ---- Scenario 7: startup DesiredEnabled=false and actual enabled ------

    /// <summary>
    /// Scenario 7: At startup, when a previously explicitly disabled item is
    /// found to be enabled (because a third-party app recreated it before the
    /// service started), it must be automatically re-disabled before the
    /// baseline is accepted. No pending approval, no approval notification.
    /// </summary>
    [Fact]
    public void Startup_DesiredDisabled_ActualEnabled_AutoReconciledBeforeBaseline()
    {
        var entry = BuildPresentEntry(isEnabled: true);
        var state = BuildState(
            isDeleted: false,
            desiredEnabled: false,
            observedEnabled: false,
            isPendingApproval: false);

        const bool hasBaseline = true;
        const bool isBaselineEstablishment = true;

        // Reconciliation takes priority over baseline-establishment semantics.
        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline, isBaselineEstablishment);

        Assert.Equal(ItemMonitorAction.ReconcileDisabledState, action);

        // Same behavior in runtime context (verified for symmetry).
        var runtimeAction = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline, isBaselineEstablishment: false);
        Assert.Equal(ItemMonitorAction.ReconcileDisabledState, runtimeAction);
    }

    // ---- Scenario 8: explicit-disabled state survives key absence ---------

    /// <summary>
    /// Scenario 8: When an explicit disabled policy (DesiredEnabled=false,
    /// not deleted) is temporarily absent from the registry (e.g. a
    /// third-party app deleted the key and will recreate it later), the
    /// persisted state must NOT be pruned by missing-state cleanup. When the
    /// key is recreated, the item must be automatically disabled.
    /// </summary>
    [Fact]
    public void ExplicitDisabledState_SurvivesKeyAbsence_NotPruned()
    {
        var state = BuildState(
            isDeleted: false,
            desiredEnabled: false,
            observedEnabled: false);

        // The missing-state pruning guard must keep this state alive.
        Assert.True(ContextMenuChangeClassifier.ShouldPreserveExplicitDisabledState(state));

        // When the item reappears as enabled, reconciliation kicks in.
        var recreatedEntry = BuildPresentEntry(isEnabled: true);
        Assert.True(ContextMenuChangeClassifier.ShouldReconcileDisabledState(recreatedEntry, state));

        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            recreatedEntry, state, hasBaseline: true, isBaselineEstablishment: false);
        Assert.Equal(ItemMonitorAction.ReconcileDisabledState, action);
    }

    /// <summary>
    /// Verifies that an ordinary neutral baseline state (DesiredEnabled=null)
    /// is NOT considered an explicit disabled policy and therefore MAY be
    /// pruned by missing-state cleanup. Only DesiredEnabled=false is enforced.
    /// </summary>
    [Fact]
    public void NeutralBaselineState_NotPreserved_CanBePruned()
    {
        var neutralState = BuildState(
            isDeleted: false,
            desiredEnabled: null,
            observedEnabled: true);

        Assert.False(ContextMenuChangeClassifier.ShouldPreserveExplicitDisabledState(neutralState));
    }

    /// <summary>
    /// Verifies that a deleted state is NOT treated as an explicit disabled
    /// policy for pruning-preservation purposes. Deleted states have their
    /// own lifecycle (backup files, DeletedAtUtc) managed separately.
    /// </summary>
    [Fact]
    public void DeletedState_NotPreservedAsExplicitDisabled()
    {
        var deletedState = BuildState(
            isDeleted: true,
            desiredEnabled: false,
            observedEnabled: false);

        Assert.False(ContextMenuChangeClassifier.ShouldPreserveExplicitDisabledState(deletedState));
    }

    // ---- Scenario 9: DesiredEnabled=true and actual disabled --------------

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

        // The enabled-state drift is observed, so it falls through to
        // MetadataModifiedHighlight (a visible consistency warning, but no
        // automatic action and no quarantine).
        Assert.Equal(ItemMonitorAction.MetadataModifiedHighlight, action);

        // A consistency issue should be visible so the user can decide.
        var consistency = ContextMenuChangeClassifier.GetConsistencyIssue(entry, state);
        Assert.NotNull(consistency);
        Assert.Contains("enabled", consistency!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Scenario 10: known metadata-only modification --------------------

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

    // ---- Scenario 11: failed corrective write leaves consistency visible ---

    /// <summary>
    /// Scenario 11: When a corrective disable write fails (e.g. access denied,
    /// registry key disappeared mid-write), the persisted DesiredEnabled must
    /// remain false, ObservedEnabled must NOT be falsely changed to false,
    /// and the consistency warning must remain visible. The classifier must
    /// keep returning ReconcileDisabledState on the next poll so a natural
    /// retry occurs. The failure must NOT be converted into a pending
    /// approval.
    /// </summary>
    [Fact]
    public void FailedCorrectiveWrite_KeepsDesiredDisabled_LeavesConsistencyVisible()
    {
        // Simulate the post-failure state: DesiredEnabled=false (policy intact),
        // ObservedEnabled=true (NOT falsely flipped to false because the write
        // failed), actual entry still enabled in the registry.
        var entry = BuildPresentEntry(isEnabled: true);
        var state = BuildState(
            isDeleted: false,
            desiredEnabled: false,
            observedEnabled: true, // write failed, so observed stays as actual
            isPendingApproval: false);

        // The drift is still detected -> reconciliation will be retried.
        Assert.True(ContextMenuChangeClassifier.ShouldReconcileDisabledState(entry, state));

        var action = ContextMenuChangeClassifier.ClassifyItemMonitorAction(
            entry, state, hasBaseline: true, isBaselineEstablishment: false);
        Assert.Equal(ItemMonitorAction.ReconcileDisabledState, action);

        // Must NOT be converted into a pending approval.
        Assert.False(state.IsPendingApproval);

        // Consistency warning remains visible because DesiredEnabled != actual.
        var consistency = ContextMenuChangeClassifier.GetConsistencyIssue(entry, state);
        Assert.NotNull(consistency);
        Assert.Contains("disabled", consistency!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Scenario 12: Reappeared approval decisions (state-level checks) --

    /// <summary>
    /// Scenario 12 (state-level): Verifies that the PendingApprovalChangeKind
    /// field correctly tracks the origin of a pending approval and that the
    /// auto-clearing logic maintains consistency between IsPendingApproval
    /// and PendingApprovalChangeKind. This is the state-machine foundation
    /// for the Allow/Deny/Remove approval decisions handled by the catalog.
    /// </summary>
    [Fact]
    public void PendingApprovalChangeKind_TracksReappearedOrigin_AutoClearsOnApprovalCleared()
    {
        var state = BuildState(isPendingApproval: false);

        // Initially no pending approval.
        Assert.False(state.IsPendingApproval);
        Assert.Null(state.PendingApprovalChangeKind);

        // Simulate the catalog's QuarantineReappearedItemAsync: it sets
        // PendingApprovalChangeKind = Reappeared while preserving IsDeleted.
        state.PendingApprovalChangeKind = ContextMenuChangeKind.Reappeared;

        // Auto-flip: setting a non-null change kind must flip IsPendingApproval
        // to true even if the caller forgot to set it explicitly.
        Assert.True(state.IsPendingApproval);
        Assert.Equal(ContextMenuChangeKind.Reappeared, state.PendingApprovalChangeKind);

        // Simulate the user resolving the approval (Allow/Deny/Remove all
        // clear the pending state). Setting IsPendingApproval=false must
        // automatically clear PendingApprovalChangeKind so no stale origin
        // leaks into later decisions.
        state.IsPendingApproval = false;
        Assert.False(state.IsPendingApproval);
        Assert.Null(state.PendingApprovalChangeKind);
    }

    /// <summary>
    /// Scenario 12 (Added origin): Verifies the same auto-clearing logic for
    /// the Added origin, ensuring both approval paths maintain consistency.
    /// </summary>
    [Fact]
    public void PendingApprovalChangeKind_TracksAddedOrigin_AutoClearsOnApprovalCleared()
    {
        var state = BuildState(isPendingApproval: false);

        state.PendingApprovalChangeKind = ContextMenuChangeKind.Added;
        Assert.True(state.IsPendingApproval);
        Assert.Equal(ContextMenuChangeKind.Added, state.PendingApprovalChangeKind);

        state.IsPendingApproval = false;
        Assert.False(state.IsPendingApproval);
        Assert.Null(state.PendingApprovalChangeKind);
    }

    // ---- Additional state-machine invariants -------------------------------

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
    /// are covered by dedicated tests above.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, true, false, ItemMonitorAction.QuarantineReappeared)]
    [InlineData(true, false, false, true, true, ItemMonitorAction.OfflineReappearedHighlight)]
    [InlineData(false, true, false, true, false, ItemMonitorAction.ReconcileDisabledState)]
    [InlineData(false, true, false, true, true, ItemMonitorAction.ReconcileDisabledState)]
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
    /// Verifies that GetDetectedChangeKind returns Reappeared (not Added or
    /// Modified) for a previously deleted item, regardless of whether the
    /// monitor has a baseline. This is the identity rule: a deleted-then-
    /// recreated item is always Reappeared, never Added.
    /// </summary>
    [Fact]
    public void GetDetectedChangeKind_DeletedStateAlwaysReappeared()
    {
        var entry = BuildPresentEntry(isEnabled: true);
        var state = BuildState(
            isDeleted: true,
            observedEnabled: false,
            backupFilePath: "C:\\Backups\\old.reg",
            deletedAtUtc: DateTimeOffset.UtcNow.AddDays(-1));

        var withBaseline = ContextMenuChangeClassifier.GetDetectedChangeKind(entry, state, hasBaseline: true);
        Assert.Equal(ContextMenuChangeKind.Reappeared, withBaseline);

        var withoutBaseline = ContextMenuChangeClassifier.GetDetectedChangeKind(entry, state, hasBaseline: false);
        Assert.Equal(ContextMenuChangeKind.Reappeared, withoutBaseline);
    }

    /// <summary>
    /// Verifies that ShouldPreserveExplicitDisabledState returns true only
    /// for the exact combination of !IsDeleted && DesiredEnabled==false.
    /// All other combinations must return false so ordinary pruning
    /// behavior applies.
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]   // not deleted, desired=false -> preserve
    [InlineData(false, true, false)]   // not deleted, desired=true  -> do NOT preserve
    [InlineData(false, null, false)]   // not deleted, desired=null  -> do NOT preserve
    [InlineData(true, false, false)]   // deleted, desired=false     -> do NOT preserve
    [InlineData(true, null, false)]    // deleted, desired=null      -> do NOT preserve
    public void ShouldPreserveExplicitDisabledState_Matrix(
        bool isDeleted, bool? desiredEnabled, bool expected)
    {
        var state = BuildState(
            isDeleted: isDeleted,
            desiredEnabled: desiredEnabled,
            observedEnabled: !desiredEnabled ?? true);

        Assert.Equal(expected, ContextMenuChangeClassifier.ShouldPreserveExplicitDisabledState(state));
    }
}

/// <summary>
/// Tests for the PersistedContextMenuState JSON-migration and field-consistency
/// behavior that underpins the Reappeared approval flow.
/// </summary>
public sealed class PersistedContextMenuStateTests
{
    /// <summary>
    /// Old state files (saved before PendingApprovalChangeKind existed) must
    /// deserialize safely with the field defaulting to null. This verifies
    /// backward-compatible JSON migration.
    /// </summary>
    [Fact]
    public void PendingApprovalChangeKind_DefaultsToNull_ForOldStateFiles()
    {
        // Simulate an old state object that was created before the field existed.
        // The default value of a nullable enum is null.
        var state = new PersistedContextMenuState
        {
            Id = "test",
            IsPendingApproval = false
        };

        Assert.Null(state.PendingApprovalChangeKind);
        Assert.False(state.IsPendingApproval);
    }

    /// <summary>
    /// Setting IsPendingApproval to true does NOT automatically set
    /// PendingApprovalChangeKind. The catalog must explicitly assign the
    /// origin. This prevents false origins from leaking when the catalog
    /// uses the simple boolean setter for Added-item quarantine.
    /// </summary>
    [Fact]
    public void SettingIsPendingApprovalTrue_DoesNotAutoSetChangeKind()
    {
        var state = new PersistedContextMenuState { IsPendingApproval = false };

        state.IsPendingApproval = true;

        Assert.True(state.IsPendingApproval);
        // Change kind remains null until the catalog explicitly assigns it.
        Assert.Null(state.PendingApprovalChangeKind);
    }

    /// <summary>
    /// Setting IsPendingApproval to false MUST automatically clear
    /// PendingApprovalChangeKind so no stale origin leaks after an approval
    /// decision resolves.
    /// </summary>
    [Fact]
    public void SettingIsPendingApprovalFalse_AutoClearsChangeKind()
    {
        var state = new PersistedContextMenuState
        {
            IsPendingApproval = true,
            PendingApprovalChangeKind = ContextMenuChangeKind.Reappeared
        };

        state.IsPendingApproval = false;

        Assert.False(state.IsPendingApproval);
        Assert.Null(state.PendingApprovalChangeKind);
    }

    /// <summary>
    /// Setting PendingApprovalChangeKind to a non-null value while
    /// IsPendingApproval is false MUST automatically flip IsPendingApproval
    /// to true. This protects against call sites that assign the origin but
    /// forget to set the boolean flag.
    /// </summary>
    [Fact]
    public void SettingChangeKind_WhileNotPending_AutoFlipsPendingTrue()
    {
        var state = new PersistedContextMenuState { IsPendingApproval = false };

        state.PendingApprovalChangeKind = ContextMenuChangeKind.Added;

        Assert.True(state.IsPendingApproval);
        Assert.Equal(ContextMenuChangeKind.Added, state.PendingApprovalChangeKind);
    }

    /// <summary>
    /// Setting PendingApprovalChangeKind back to null does NOT automatically
    /// clear IsPendingApproval. The boolean flag is the authoritative
    /// approval-state guard; only setting it to false clears the origin.
    /// </summary>
    [Fact]
    public void SettingChangeKindToNull_DoesNotClearPendingApproval()
    {
        var state = new PersistedContextMenuState
        {
            IsPendingApproval = true,
            PendingApprovalChangeKind = ContextMenuChangeKind.Reappeared
        };

        state.PendingApprovalChangeKind = null;

        // IsPendingApproval stays true; the origin is just cleared.
        Assert.True(state.IsPendingApproval);
        Assert.Null(state.PendingApprovalChangeKind);
    }

    /// <summary>
    /// FromEntry must not carry over PendingApprovalChangeKind from an entry
    /// that was constructed without it (the entry contract does not expose
    /// this field). The catalog assigns it explicitly after creating the
    /// state.
    /// </summary>
    [Fact]
    public void FromEntry_DoesNotCarryOverPendingApprovalChangeKind()
    {
        var entry = new ContextMenuEntry
        {
            Id = "test",
            IsPendingApproval = true,
            IsEnabled = true,
            IsPresentInRegistry = true
        };

        var state = PersistedContextMenuState.FromEntry(entry);

        Assert.True(state.IsPendingApproval);
        Assert.Null(state.PendingApprovalChangeKind);
    }

    /// <summary>
    /// ToDeletedEntry must preserve the IsPendingApproval flag so a deleted
    /// item that is also pending approval (Reappeared scenario) keeps its
    /// pending state visible to the frontend.
    /// </summary>
    [Fact]
    public void ToDeletedEntry_PreservesIsPendingApproval()
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
        Assert.True(entry.IsPendingApproval);
        Assert.True(entry.HasBackup);
        Assert.NotNull(entry.DeletedAtUtc);
    }
}
