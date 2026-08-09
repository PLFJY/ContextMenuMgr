using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// The runtime action the monitor should take for a detected item.
/// </summary>
public enum ItemMonitorAction
{
    /// <summary>No action: absorb into the known-items baseline silently.</summary>
    None,

    /// <summary>
    /// The item drifts away from an explicit disabled policy. Automatically
    /// re-disable it without user approval or notification. Applies to both
    /// startup and runtime contexts.
    /// </summary>
    ReconcileDisabledState,

    /// <summary>
    /// Runtime only: a brand-new unknown item was created. Quarantine it and
    /// ask the user for an approval decision.
    /// </summary>
    QuarantineAdded,

    /// <summary>
    /// Runtime only: a previously deleted item reappeared. Quarantine it while
    /// preserving deletion provenance, and ask the user for a decision.
    /// </summary>
    QuarantineReappeared,

    /// <summary>
    /// Startup/offline only: an unknown item appeared while the monitor was not
    /// running. Expose it as an Added highlight but do not quarantine or notify.
    /// </summary>
    OfflineAddedHighlight,

    /// <summary>
    /// Startup/offline only: a previously deleted item reappeared while the
    /// monitor was not running. Expose the Reappeared consistency warning but
    /// do not quarantine or notify.
    /// </summary>
    OfflineReappearedHighlight,

    /// <summary>
    /// A known item changed metadata only. Keep the Modified highlight; do not
    /// quarantine, roll back, or notify.
    /// </summary>
    MetadataModifiedHighlight
}

/// <summary>
/// Pure, deterministic classification helpers for the external context-menu change
/// state machine. These functions never touch the registry or the state store, so
/// they can be unit tested directly. The catalog and monitor delegate to them so
/// the state-machine matrix stays in one auditable place.
/// </summary>
internal static class ContextMenuChangeClassifier
{
    /// <summary>
    /// Computes the detected change kind for a present registry entry.
    /// </summary>
    public static ContextMenuChangeKind GetDetectedChangeKind(
        ContextMenuEntry entry,
        PersistedContextMenuState? state,
        bool hasBaseline)
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

    /// <summary>
    /// Builds the human-facing change details string for the given change kind.
    /// </summary>
    public static string? GetDetectedChangeDetails(
        ContextMenuEntry entry,
        PersistedContextMenuState? state,
        ContextMenuChangeKind changeKind)
    {
        return changeKind switch
        {
            ContextMenuChangeKind.Added => "This item is new compared with the last saved context menu snapshot.",
            ContextMenuChangeKind.Reappeared => "This item was previously deleted through the app, but it has reappeared in the registry.",
            ContextMenuChangeKind.Modified when state is not null => BuildModifiedDetails(entry, state),
            _ => null
        };
    }

    /// <summary>
    /// Computes the consistency issue string for a present entry, or null when consistent.
    /// </summary>
    public static string? GetConsistencyIssue(ContextMenuEntry entry, PersistedContextMenuState? state)
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

    /// <summary>
    /// Returns true when the persisted state represents an explicit disabled policy
    /// that must survive temporary registry key absence. Such states are never pruned
    /// by missing-state cleanup so a later third-party recreation can be reconciled.
    /// </summary>
    public static bool ShouldPreserveExplicitDisabledState(PersistedContextMenuState state)
    {
        return !state.IsDeleted && state.DesiredEnabled == false;
    }

    /// <summary>
    /// Returns true when the actual entry drifts away from an explicit disabled
    /// policy and must be automatically re-disabled without user approval.
    /// </summary>
    public static bool ShouldReconcileDisabledState(ContextMenuEntry entry, PersistedContextMenuState? state)
    {
        return state is not null
               && entry.CanToggle
               && !state.IsDeleted
               && state.DesiredEnabled == false
               && entry.IsEnabled
               && !state.IsPendingApproval;
    }

    /// <summary>
    /// Returns true when the entry has any observed metadata or enabled-state change
    /// relative to the persisted state.
    /// </summary>
    public static bool HasObservedChange(ContextMenuEntry entry, PersistedContextMenuState state)
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
               || state.Windows11SourceKind != entry.Windows11SourceKind
               || state.IsProtectedSystemItem != entry.IsProtectedSystemItem
               || state.OnlyWithShift != entry.OnlyWithShift
               || state.OnlyInExplorer != entry.OnlyInExplorer
               || state.NoWorkingDirectory != entry.NoWorkingDirectory
               || state.NeverDefault != entry.NeverDefault
               || state.ShowAsDisabledIfHidden != entry.ShowAsDisabledIfHidden
               || !string.Equals(state.Notes, entry.Notes, StringComparison.Ordinal)
               || !string.Equals(state.SourceRootPath, entry.SourceRootPath, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasExternalEnabledStateChange(ContextMenuEntry entry, PersistedContextMenuState state)
    {
        if (state.DesiredEnabled is { } desiredEnabled)
        {
            return entry.IsEnabled != desiredEnabled;
        }

        return false;
    }

    /// <summary>
    /// Classifies the monitor action to take for a present registry entry.
    ///
    /// This is the single source of truth for the external-change state machine.
    /// The reconciliation pass and the classification pass both call this function
    /// and then filter by the returned action.
    ///
    /// State-machine matrix:
    /// - ShouldReconcileDisabledState -> ReconcileDisabledState (auto re-disable, no approval)
    /// - Already pending approval -> None (don't re-quarantine)
    /// - state.IsDeleted -> QuarantineReappeared (runtime) / OfflineReappearedHighlight (startup)
    /// - state is null + hasBaseline -> QuarantineAdded (runtime) / OfflineAddedHighlight (startup)
    /// - state is null + !hasBaseline -> None (first run, adopt as baseline)
    /// - HasObservedChange -> MetadataModifiedHighlight
    /// - otherwise -> None
    /// </summary>
    /// <param name="entry">The actual registry entry as observed in the current snapshot.</param>
    /// <param name="state">The persisted state for this item, or null if no state exists.</param>
    /// <param name="hasBaseline">True when the monitor has an established known-items baseline.</param>
    /// <param name="isBaselineEstablishment">
    /// True when the monitor is establishing a startup or interactive-session baseline
    /// (was not running when the change happened). In this context, Added and Reappeared
    /// items are highlighted but not quarantined or notified.
    /// </param>
    public static ItemMonitorAction ClassifyItemMonitorAction(
        ContextMenuEntry entry,
        PersistedContextMenuState? state,
        bool hasBaseline,
        bool isBaselineEstablishment)
    {
        // Explicit disabled-state drift: auto re-disable without approval.
        // This applies to both startup and runtime contexts.
        if (ShouldReconcileDisabledState(entry, state))
        {
            return ItemMonitorAction.ReconcileDisabledState;
        }

        // Already pending approval: don't re-quarantine or re-notify.
        if (state is not null && state.IsPendingApproval)
        {
            return ItemMonitorAction.None;
        }

        // Previously deleted item reappeared.
        if (state is not null && state.IsDeleted)
        {
            return isBaselineEstablishment
                ? ItemMonitorAction.OfflineReappearedHighlight
                : ItemMonitorAction.QuarantineReappeared;
        }

        // Completely unknown item.
        if (state is null)
        {
            if (!hasBaseline)
            {
                // First run with empty state database: adopt as baseline.
                return ItemMonitorAction.None;
            }

            return isBaselineEstablishment
                ? ItemMonitorAction.OfflineAddedHighlight
                : ItemMonitorAction.QuarantineAdded;
        }

        // Known item with metadata-only change (including desired=true/actual=false
        // or failed reconciliation drift). Highlight only, no quarantine.
        if (HasObservedChange(entry, state))
        {
            return ItemMonitorAction.MetadataModifiedHighlight;
        }

        return ItemMonitorAction.None;
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
}
