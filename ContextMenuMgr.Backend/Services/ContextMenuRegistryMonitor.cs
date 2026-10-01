using System.Collections.Concurrent;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

public sealed record ContextMenuDetectedEventArgs(ContextMenuEntry Item, BackendUserContext? UserContext);

// Timed polling keeps the scaffold simple while still showing how the service can
// push real-time-ish notifications into the frontend over IPC.
/// <summary>
/// Represents the context Menu Registry Monitor.
/// </summary>
public sealed class ContextMenuRegistryMonitor
{
    private readonly ContextMenuRegistryCatalog _catalog;
    private readonly FileLogger _logger;
    private readonly Func<BackendUserContext?> _resolveUserContext;
    private readonly TimeSpan _pollInterval;
    private readonly ConcurrentQueue<(string? Sid, IReadOnlyList<ContextMenuEntry> Items)> _committedSwitches = new();
    private readonly TaskCompletionSource _baselineEstablished = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        : this(catalog, logger, userContextResolver.TryResolveInteractiveUserFallback, pollInterval)
    {
    }

    internal ContextMenuRegistryMonitor(
        ContextMenuRegistryCatalog catalog,
        FileLogger logger,
        Func<BackendUserContext?> resolveUserContext,
        TimeSpan? pollInterval = null)
    {
        _catalog = catalog;
        _logger = logger;
        _resolveUserContext = resolveUserContext;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        _catalog.VerifiedSwitchCommitted += (context, items) =>
            _committedSwitches.Enqueue((context?.Sid, items));
    }

    public ContextMenuRegistryCatalog Catalog => _catalog;

    internal Task BaselineEstablished => _baselineEstablished.Task;

    internal Task Completion => _monitorTask ?? Task.CompletedTask;

    public event EventHandler<ContextMenuDetectedEventArgs>? ItemDetected;

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
        // Startup is an offline comparison boundary. Do not silently reconcile
        // disabled-to-enabled drift here: rule 5 requires both switch directions
        // to remain visible as Modified until the user handles them.
        ClearCommittedSwitches();
        var initialScan = await ReadSnapshotAsync(cancellationToken);
        var baselineSid = initialScan.UserContext?.Sid;
        var knownItems = initialScan.Items
            .Where(static item => item.IsPresentInRegistry && !item.IsDeleted)
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        // Consume any SuppressNextDetection flags for items present in the
        // initial baseline so they do not leak into later runtime polls.
        foreach (var item in knownItems.Values)
        {
            await _catalog.TryConsumeSuppressedDetectionAsync(item.Id, cancellationToken);
        }

        await _logger.LogAsync($"RegistryMonitorBaseline: VisibleItemCount={knownItems.Count}.", cancellationToken);
        _baselineEstablished.TrySetResult();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _logger.LogAsync($"RegistryMonitorDebounceWait: DelayMs={_pollInterval.TotalMilliseconds}.", cancellationToken);
                await Task.Delay(_pollInterval, cancellationToken);

                if (!_interactiveBaselineResetRequested)
                {
                    ApplyCommittedSwitches(knownItems, baselineSid);
                }

                var currentScan = await ReadSnapshotAsync(cancellationToken);
                if (currentScan.UserContext is null)
                {
                    // No user hive is authoritative while an interactive
                    // session is unavailable. Keep the settled baseline and
                    // retry when a user context returns.
                    _interactiveBaselineResetRequested = true;
                    continue;
                }

                if (!string.Equals(baselineSid, currentScan.UserContext.Sid, StringComparison.OrdinalIgnoreCase))
                {
                    _interactiveBaselineResetRequested = true;
                }

                var currentSnapshot = currentScan.Items
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
                    // The interactive session event can arrive while the user hive is
                    // still loading. If the current snapshot is far smaller than the
                    // persisted baseline, defer the rebuild; otherwise the next poll
                    // would classify the still-invisible per-user entries as Added and
                    // spam quarantine/ItemDetected notifications on every startup.
                    var persistedActiveCount = await _catalog.GetPersistedActiveStateCountAsync(cancellationToken);
                    if (persistedActiveCount > 0
                        && currentSnapshot.Count < Math.Max(1, (int)(persistedActiveCount * 0.8)))
                    {
                        await _logger.LogAsync(
                            $"Interactive-session baseline deferred: VisibleCount={currentSnapshot.Count}, " +
                            $"PersistedActiveCount={persistedActiveCount}. The interactive user hive may not be fully loaded yet.",
                            cancellationToken);
                        continue;
                    }

                    knownItems = currentSnapshot.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
                    baselineSid = currentScan.UserContext.Sid;

                    // Consume SuppressNextDetection flags for items present in the
                    // new baseline so they do not suppress genuine later recreations.
                    foreach (var item in knownItems.Values)
                    {
                        await _catalog.TryConsumeSuppressedDetectionAsync(item.Id, cancellationToken);
                    }

                    _interactiveBaselineResetRequested = false;
                    await _logger.LogAsync(
                        $"Interactive-session snapshot settled. Rebuilt monitor baseline with {knownItems.Count} visible items.",
                        cancellationToken);
                    continue;
                }

                // Rule 3 applies only to a transition observed while this monitor
                // is running. An item that was already enabled in the startup or
                // post-session baseline is offline drift and remains Modified.
                var runtimeReenabledItems = currentSnapshot
                    .Where(item => knownItems.TryGetValue(item.Id, out var previous)
                                   && !previous.IsEnabled
                                   && item.IsEnabled)
                    .ToArray();
                IReadOnlySet<string> failedReconciliationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (runtimeReenabledItems.Length > 0)
                {
                    var userContext = currentScan.UserContext;
                    var reconciliation = await _catalog.ReconcilePersistedDisabledItemsAsync(
                        runtimeReenabledItems,
                        cancellationToken,
                        userContext);

                    if (reconciliation.HasChanges)
                    {
                        await _logger.LogAsync(
                            $"RuntimeDisabledStateReconciliation: Reconciled={reconciliation.ReconciledItemIds.Count}, " +
                            $"Failed={reconciliation.FailedItemIds.Count}, ReloadingSnapshot=True.",
                            cancellationToken);
                        currentScan = await ReadSnapshotAsync(cancellationToken);
                        currentSnapshot = currentScan.Items
                            .Where(static item => item.IsPresentInRegistry && !item.IsDeleted)
                            .ToList();
                    }

                    failedReconciliationIds = reconciliation.FailedItemIds
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }

                // Detect new runtime items.
                foreach (var item in currentSnapshot.Where(item => !knownItems.ContainsKey(item.Id)))
                {
                    if (await _catalog.TryConsumeSuppressedDetectionAsync(item.Id, cancellationToken))
                    {
                        knownItems[item.Id] = item;
                        await _logger.LogAsync($"Suppressed review prompt for restored menu item: {item.DisplayName}", cancellationToken);
                        continue;
                    }

                    // Only genuinely new runtime items enter approval. Modified
                    // items remain visible as external changes, while deleted
                    // identities have already been removed from the baseline.
                    if (item.CanToggle
                        && item.DetectedChangeKind == ContextMenuChangeKind.Added)
                    {
                        await _logger.LogAsync(
                            $"RegistryMonitorChangeDetected: Kind={item.DetectedChangeKind}, ItemId={item.Id}, " +
                            $"DisplayName={item.DisplayName}, Root={item.SourceRootPath}, Path={item.RegistryPath}.",
                            cancellationToken);
                        ItemDetected?.Invoke(this, new ContextMenuDetectedEventArgs(item, currentScan.UserContext));
                        continue;
                    }

                    knownItems[item.Id] = item;
                }

                // Update knownItems from the post-reconciliation snapshot so
                // ContextMenuMgr does not detect its own corrective write as a
                // new external change on the next poll.
                foreach (var item in currentSnapshot)
                {
                    // The runtime event is asynchronous. Keep a new Added item
                    // outside knownItems until quarantine has committed its
                    // approval state; a failed write is retried next poll.
                    if (!knownItems.ContainsKey(item.Id)
                        && item.CanToggle
                        && item.DetectedChangeKind == ContextMenuChangeKind.Added)
                    {
                        continue;
                    }

                    // Keep the preceding disabled observation after a failed
                    // corrective write so the same runtime transition is retried
                    // on the next poll.
                    if (failedReconciliationIds.Contains(item.Id))
                    {
                        continue;
                    }

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
    /// Reads the current snapshot in the interactive user's registry context.
    /// Runtime transition reconciliation is deliberately performed by the
    /// monitor only after this snapshot has been compared with its in-memory
    /// baseline.
    /// </summary>
    private async Task<(IReadOnlyList<ContextMenuEntry> Items, BackendUserContext? UserContext)> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        // The monitor runs independently of frontend pipe connections. Without an
        // explicit user context, Windows 11 packaged COM entries and per-user
        // shell entries under HKEY_USERS\<sid> cannot be enumerated correctly
        // when the interactive session is temporarily unavailable (screen lock,
        // UAC elevation, fast-user switch). This causes mass false-negative
        // disappearances that corrupt the persisted state baseline.
        var userContext = _resolveUserContext();
        return (await _catalog.GetSnapshotAsync(cancellationToken, userContext), userContext);
    }

    private void ApplyCommittedSwitches(Dictionary<string, ContextMenuEntry> knownItems, string? baselineSid)
    {
        while (_committedSwitches.TryDequeue(out var committed))
        {
            if (baselineSid is null
                || !string.Equals(committed.Sid, baselineSid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var item in committed.Items.Where(static item => item.IsPresentInRegistry && !item.IsDeleted))
            {
                knownItems[item.Id] = item;
            }
        }
    }

    private void ClearCommittedSwitches()
    {
        while (_committedSwitches.TryDequeue(out _))
        {
        }
    }
}
