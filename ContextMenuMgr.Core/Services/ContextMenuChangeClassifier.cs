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
    /// re-disable it without user approval or notification. The monitor invokes
    /// this only for a transition observed between settled runtime snapshots.
    /// </summary>
    ReconcileDisabledState,

    /// <summary>
    /// Runtime only: a brand-new unknown item was created. Quarantine it and
    /// ask the user for an approval decision.
    /// </summary>
    QuarantineAdded,

    /// <summary>
    /// Startup/offline only: an unknown item appeared while the monitor was not
    /// running. Expose it as an Added highlight but do not quarantine or notify.
    /// </summary>
    OfflineAddedHighlight,

    /// <summary>
    /// A known item changed enabled state. Keep the Modified highlight; do not
    /// quarantine, roll back, or notify.
    /// </summary>
    EnabledStateModifiedHighlight
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
            // Deleted states are recovery-only and do not retain monitoring
            // identity. A live key with the same Id follows the ordinary Added
            // rule once the catalog discards the stale recovery record.
            return hasBaseline ? ContextMenuChangeKind.Added : ContextMenuChangeKind.None;
        }

        return HasExternalEnabledStateChange(entry, state)
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
            ContextMenuChangeKind.Modified when state is not null && HasExternalEnabledStateChange(entry, state)
                => "This item changed outside the app. Updated fields: enabled state.",
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
            return null;
        }

        // An enabled-state difference is a classified external modification,
        // not an unclassified consistency error. Desired=false/actual=true is
        // reconciled by the backend; desired=true/actual=false remains a
        // dismissible Modified change until the user adopts the registry state.
        return null;
    }

    /// <summary>
    /// Returns true when a missing state belongs to an active monitoring baseline
    /// and must be removed after the snapshot is confirmed stable. Desired state
    /// does not keep a registry identity alive after its key has been deleted.
    /// </summary>
    public static bool ShouldRemoveMissingState(PersistedContextMenuState state)
    {
        return !state.IsDeleted;
    }

    /// <summary>
    /// Returns true when the actual entry drifts away from an explicit disabled
    /// policy and must be automatically re-disabled without user approval.
    /// </summary>
    public static bool ShouldReconcileDisabledState(ContextMenuEntry entry, PersistedContextMenuState? state)
    {
        return state is not null
               && entry.CanAutomaticallyDisable
               && !state.IsDeleted
               && state.DesiredEnabled == false
               && entry.IsEnabled
               && !state.IsPendingApproval;
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
    /// - state.IsDeleted -> treat as unknown Added; deleted state is recovery-only
    /// - state is null + hasBaseline -> QuarantineAdded (runtime) / OfflineAddedHighlight (startup)
    /// - state is null + !hasBaseline -> None (first run, adopt as baseline)
    /// - HasExternalEnabledStateChange -> EnabledStateModifiedHighlight
    /// - otherwise -> None
    /// </summary>
    /// <param name="entry">The actual registry entry as observed in the current snapshot.</param>
    /// <param name="state">The persisted state for this item, or null if no state exists.</param>
    /// <param name="hasBaseline">True when the monitor has an established known-items baseline.</param>
    /// <param name="isBaselineEstablishment">
    /// True when the monitor is establishing a startup or interactive-session baseline
    /// (was not running when the change happened). In this context, newly added items are
    /// highlighted but not quarantined or notified.
    /// </param>
    public static ItemMonitorAction ClassifyItemMonitorAction(
        ContextMenuEntry entry,
        PersistedContextMenuState? state,
        bool hasBaseline,
        bool isBaselineEstablishment)
    {
        // Only a transition observed between settled runtime snapshots is
        // eligible for silent correction. Startup/offline drift remains a
        // Modified change for user review.
        if (!isBaselineEstablishment && ShouldReconcileDisabledState(entry, state))
        {
            return ItemMonitorAction.ReconcileDisabledState;
        }

        // Already pending approval: don't re-quarantine or re-notify.
        if (state is not null && state.IsPendingApproval)
        {
            return ItemMonitorAction.None;
        }

        // Deleted recovery metadata is excluded from monitoring identity.
        if (state is not null && state.IsDeleted)
        {
            return !hasBaseline
                ? ItemMonitorAction.None
                : isBaselineEstablishment
                    ? ItemMonitorAction.OfflineAddedHighlight
                    : ItemMonitorAction.QuarantineAdded;
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

        // Known item with an enabled-state change (including desired=true/actual=false
        // or failed reconciliation drift). Highlight only, no quarantine.
        if (HasExternalEnabledStateChange(entry, state))
        {
            return ItemMonitorAction.EnabledStateModifiedHighlight;
        }

        return ItemMonitorAction.None;
    }
}
