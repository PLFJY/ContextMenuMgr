using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

// Timed polling keeps the scaffold simple while still showing how the service can
// push real-time-ish notifications into the frontend over IPC.
/// <summary>
/// Represents the context Menu Registry Monitor.
/// </summary>
public sealed class ContextMenuRegistryMonitor
{
    private readonly ContextMenuRegistryCatalog _catalog;
    private readonly FileLogger _logger;
    private readonly BackendUserContextResolver _userContextResolver;
    private readonly TimeSpan _pollInterval;
    private Task? _monitorTask;
    private volatile bool _interactiveBaselineResetRequested;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextMenuRegistryMonitor"/> class.
    /// </summary>
    public ContextMenuRegistryMonitor(
        ContextMenuRegistryCatalog catalog,
        FileLogger logger,
        BackendUserContextResolver userContextResolver,
        TimeSpan? pollInterval = null)
    {
        _catalog = catalog;
        _logger = logger;
        _userContextResolver = userContextResolver;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    }

    public ContextMenuRegistryCatalog Catalog => _catalog;

    public event EventHandler<ContextMenuEntry>? ItemDetected;

    /// <summary>
    /// Requests that the monitor rebuild its runtime baseline from the first snapshot
    /// captured after an interactive user session becomes available.
    /// </summary>
    public void NotifyInteractiveSessionObserved()
    {
        _interactiveBaselineResetRequested = true;
    }

    /// <summary>
    /// Executes start.
    /// </summary>
    public void Start(CancellationToken cancellationToken)
    {
        _logger.LogFireAndForget($"RegistryMonitorStart: PollIntervalMs={_pollInterval.TotalMilliseconds}.");
        _monitorTask ??= Task.Run(() => MonitorLoopAsync(cancellationToken), cancellationToken);
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        // Startup baseline: reconcile explicit disabled-state drift before
        // establishing the known-items baseline. This ensures third-party
        // applications that re-created their shell keys before the service
        // started are automatically re-disabled without user approval.
        var initialSnapshot = await ReconcileAndRefreshSnapshotAsync(cancellationToken);
        var knownItems = initialSnapshot
            .Where(static item => item.IsPresentInRegistry && !item.IsDeleted)
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        // Consume any SuppressNextDetection flags for items present in the
        // initial baseline so they do not leak into later runtime polls.
        foreach (var item in knownItems.Values)
        {
            await _catalog.TryConsumeSuppressedDetectionAsync(item.Id, cancellationToken);
        }

        await _logger.LogAsync($"RegistryMonitorBaseline: VisibleItemCount={knownItems.Count}.", cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _logger.LogAsync($"RegistryMonitorDebounceWait: DelayMs={_pollInterval.TotalMilliseconds}.", cancellationToken);
                await Task.Delay(_pollInterval, cancellationToken);

                var currentSnapshot = (await ReconcileAndRefreshSnapshotAsync(cancellationToken))
                    .Where(static item => item.IsPresentInRegistry && !item.IsDeleted)
                    .ToList();

                var newIds = currentSnapshot.Count(item => !knownItems.ContainsKey(item.Id));
                var deletedIds = knownItems.Keys.Except(currentSnapshot.Select(item => item.Id), StringComparer.OrdinalIgnoreCase).Count();
                await _logger.LogAsync($"RegistryMonitorSnapshotComparison: PreviousCount={knownItems.Count}, CurrentCount={currentSnapshot.Count}, NewItemIds={newIds}, DeletedItemIds={deletedIds}.", cancellationToken);

                if (_interactiveBaselineResetRequested)
                {
                    // The first post-login snapshot is used to rebuild the monitor
                    // baseline instead of generating "new item" events. Many per-user
                    // HKCU/HKU handlers and packaged COM registrations only become
                    // visible once the interactive shell is fully online.
                    //
                    // Reconciliation was already performed inside
                    // ReconcileAndRefreshSnapshotAsync, so explicit disabled-state
                    // drift has been corrected before we accept this baseline.
                    knownItems = currentSnapshot.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

                    // Consume SuppressNextDetection flags for items present in the
                    // new baseline so they do not suppress genuine later recreations.
                    foreach (var item in knownItems.Values)
                    {
                        await _catalog.TryConsumeSuppressedDetectionAsync(item.Id, cancellationToken);
                    }

                    _catalog.MarkInteractiveSessionSnapshotSettled();
                    _interactiveBaselineResetRequested = false;
                    await _logger.LogAsync(
                        $"Interactive-session snapshot settled. Rebuilt monitor baseline with {knownItems.Count} visible items.",
                        cancellationToken);
                    continue;
                }

                // Detect new and reappeared items.
                foreach (var item in currentSnapshot.Where(item => !knownItems.ContainsKey(item.Id)))
                {
                    if (await _catalog.TryConsumeSuppressedDetectionAsync(item.Id, cancellationToken))
                    {
                        knownItems[item.Id] = item;
                        await _logger.LogAsync($"Suppressed review prompt for restored menu item: {item.DisplayName}", cancellationToken);
                        continue;
                    }

                    // Only trigger ItemDetected for genuinely new (Added) or
                    // previously-deleted-then-recreated (Reappeared) items.
                    // Modified items and items appearing under additional roots
                    // after the baseline are silently absorbed.
                    if (item.DetectedChangeKind is ContextMenuChangeKind.Added
                        or ContextMenuChangeKind.Reappeared)
                    {
                        await _logger.LogAsync(
                            $"RegistryMonitorChangeDetected: Kind={item.DetectedChangeKind}, ItemId={item.Id}, " +
                            $"DisplayName={item.DisplayName}, Root={item.SourceRootPath}, Path={item.RegistryPath}.",
                            cancellationToken);
                        ItemDetected?.Invoke(this, item);
                        continue;
                    }

                    knownItems[item.Id] = item;
                }

                // Update knownItems from the post-reconciliation snapshot so
                // ContextMenuMgr does not detect its own corrective write as a
                // new external change on the next poll.
                foreach (var item in currentSnapshot)
                {
                    knownItems[item.Id] = item;
                }

                // Remove items that are no longer present.
                foreach (var removedId in knownItems.Keys.Except(currentSnapshot.Select(item => item.Id), StringComparer.OrdinalIgnoreCase).ToList())
                {
                    await _logger.LogAsync($"RegistryMonitorChangeDetected: Kind=Deleted, ItemId={removedId}.", cancellationToken);
                    knownItems.Remove(removedId);
                }
            }
            catch (OperationCanceledException)
            {
                await _logger.LogAsync("RegistryMonitorStop: Reason=CancellationRequested.", CancellationToken.None);
                break;
            }
            catch (Exception ex)
            {
                await _logger.LogAsync(RuntimeLogLevel.Warning, $"Registry monitor error: {ex}", cancellationToken);
            }
        }
    }

    /// <summary>
    /// Reads a snapshot, reconciles explicit disabled-state drift, and reloads
    /// the snapshot once if reconciliation changed anything. This ensures the
    /// monitor always classifies changes against the post-reconciliation state
    /// rather than detecting its own corrective writes as external changes.
    /// </summary>
    private async Task<IReadOnlyList<ContextMenuEntry>> ReconcileAndRefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        // The monitor runs independently of frontend pipe connections. Without an
        // explicit user context, Windows 11 packaged COM entries and per-user
        // shell entries under HKEY_USERS\<sid> cannot be enumerated correctly
        // when the interactive session is temporarily unavailable (screen lock,
        // UAC elevation, fast-user switch). This causes mass false-negative
        // disappearances that corrupt the persisted state baseline.
        var userContext = _userContextResolver.TryResolveInteractiveUserFallback();
        var snapshot = await _catalog.GetSnapshotAsync(cancellationToken, userContext);

        var result = await _catalog.ReconcilePersistedDisabledItemsAsync(snapshot, cancellationToken);

        if (result.HasChanges)
        {
            await _logger.LogAsync(
                $"DesiredStateReconciliationPass: Reconciled={result.ReconciledItemIds.Count}, " +
                $"Failed={result.FailedItemIds.Count}, ReloadingSnapshot=True.",
                cancellationToken);
            snapshot = await _catalog.GetSnapshotAsync(cancellationToken, userContext);
        }

        return snapshot;
    }
}
