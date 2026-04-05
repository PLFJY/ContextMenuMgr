using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

// Timed polling keeps the scaffold simple while still showing how the service can
// push real-time-ish notifications into the frontend over IPC.
public sealed class ContextMenuRegistryMonitor
{
    private readonly ContextMenuRegistryCatalog _catalog;
    private readonly FileLogger _logger;
    private readonly TimeSpan _pollInterval;
    private Task? _monitorTask;

    public ContextMenuRegistryMonitor(
        ContextMenuRegistryCatalog catalog,
        FileLogger logger,
        TimeSpan? pollInterval = null)
    {
        _catalog = catalog;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    }

    public ContextMenuRegistryCatalog Catalog => _catalog;

    public event EventHandler<ContextMenuEntry>? ItemDetected;

    public void Start(CancellationToken cancellationToken)
    {
        _monitorTask ??= Task.Run(() => MonitorLoopAsync(cancellationToken), cancellationToken);
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        var knownItems = (await _catalog.GetSnapshotAsync(cancellationToken))
            .Where(static item => item.IsPresentInRegistry && !item.IsDeleted)
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, cancellationToken);

                var currentSnapshot = (await _catalog.GetSnapshotAsync(cancellationToken))
                    .Where(static item => item.IsPresentInRegistry && !item.IsDeleted)
                    .ToList();

                foreach (var item in currentSnapshot.Where(item => !knownItems.ContainsKey(item.Id)))
                {
                    knownItems[item.Id] = item;
                    if (await _catalog.TryConsumeSuppressedDetectionAsync(item.Id, cancellationToken))
                    {
                        await _logger.LogAsync($"Suppressed review prompt for restored menu item: {item.DisplayName}", cancellationToken);
                        continue;
                    }

                    await _logger.LogAsync($"Detected new menu item: {item.DisplayName}", cancellationToken);
                    ItemDetected?.Invoke(this, item);
                }

                foreach (var item in currentSnapshot)
                {
                    if (!knownItems.TryGetValue(item.Id, out var previous))
                    {
                        continue;
                    }

                    if (RequiresApprovalForExternalReenable(previous, item))
                    {
                        knownItems[item.Id] = item;
                        await _logger.LogAsync($"Detected externally re-enabled menu item: {item.DisplayName}", cancellationToken);
                        ItemDetected?.Invoke(this, item);
                        continue;
                    }

                    knownItems[item.Id] = item;
                }

                foreach (var removedId in knownItems.Keys.Except(currentSnapshot.Select(item => item.Id), StringComparer.OrdinalIgnoreCase).ToList())
                {
                    knownItems.Remove(removedId);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await _logger.LogAsync($"Registry monitor error: {ex.Message}", cancellationToken);
            }
        }
    }

    private static bool RequiresApprovalForExternalReenable(ContextMenuEntry previous, ContextMenuEntry current)
    {
        return !previous.IsPendingApproval
               && !previous.IsEnabled
               && current.IsEnabled
               && current.DetectedChangeKind == ContextMenuChangeKind.Modified
               && current.HasConsistencyIssue;
    }
}
