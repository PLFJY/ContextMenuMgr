using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using System.IO;

namespace ContextMenuMgr.Backend.Hosting;

/// <summary>
/// Represents the backend Runtime.
/// </summary>
public sealed class BackendRuntime : IDisposable
{
    private static readonly TimeSpan ApprovalNotificationDedupWindow = TimeSpan.FromMinutes(5);
    private readonly FileLogger _logger;
    private readonly ContextMenuRegistryMonitor _monitor;
    private readonly NamedPipeBackendServer _pipeServer;
    private readonly FrontendAutostartLauncher _frontendAutostartLauncher;
    private readonly Lock _approvalNotificationSyncRoot = new();
    private readonly Dictionary<string, DateTimeOffset> _recentApprovalNotificationKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _ensureTrayHostOnStartup;
    private bool _shutdownFrontendOnStop = true;
    private CancellationTokenSource? _lifetimeCts;
    private static readonly string KeepFrontendOnStopMarkerPath = Path.Combine(
        RuntimePaths.DataDirectory,
        ServiceMetadata.KeepFrontendOnStopMarkerFileName);

    public event EventHandler? StopRequested;

    private BackendRuntime(
        FileLogger logger,
        ContextMenuRegistryMonitor monitor,
        NamedPipeBackendServer pipeServer,
        FrontendAutostartLauncher frontendAutostartLauncher)
    {
        _logger = logger;
        _monitor = monitor;
        _pipeServer = pipeServer;
        _frontendAutostartLauncher = frontendAutostartLauncher;
    }

    /// <summary>
    /// Creates default.
    /// </summary>
    public static BackendRuntime CreateDefault()
    {
        TryMigrateLegacyRuntimeFiles();
        var logger = new FileLogger(RuntimePaths.BackendLogPath);
        var stateStore = new ContextMenuStateStore(RuntimePaths.StateDatabasePath);
        var protectionSettingsStore = new BackendProtectionSettingsStore(Path.Combine(RuntimePaths.DataDirectory, "backend-protection-settings.json"));
        var backupService = new RegistryBackupService(RuntimePaths.DeletedBackupsDirectory);
        var catalog = new ContextMenuRegistryCatalog(logger, stateStore, backupService, protectionSettingsStore);
        var monitor = new ContextMenuRegistryMonitor(catalog, logger);
        var pipeServer = new NamedPipeBackendServer(catalog, logger);
        var frontendAutostartLauncher = new FrontendAutostartLauncher(AppContext.BaseDirectory);

        return new BackendRuntime(logger, monitor, pipeServer, frontendAutostartLauncher);
    }

    private static void TryMigrateLegacyRuntimeFiles()
    {
        TryCopyIfMissing(RuntimePaths.LegacyStateDatabasePath, RuntimePaths.StateDatabasePath);
        TryCopyIfMissing(RuntimePaths.LegacyBackendProtectionSettingsPath, Path.Combine(RuntimePaths.DataDirectory, "backend-protection-settings.json"));
    }

    private static void TryCopyIfMissing(string sourcePath, string destinationPath)
    {
        try
        {
            if (!File.Exists(sourcePath) || File.Exists(destinationPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Executes run Console Async.
    /// </summary>
    public async Task RunConsoleAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        _lifetimeCts = cts;

        Console.CancelKeyPress += OnConsoleCancelKeyPress;
        await StartAsync(cts.Token);

        Console.WriteLine("ContextMenuMgr backend is running in console mode.");
        Console.WriteLine("Use --service or install the executable as a Windows Service for production.");

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Console.CancelKeyPress -= OnConsoleCancelKeyPress;
            await StopAsync();
        }
    }

    /// <summary>
    /// Starts async.
    /// </summary>
    public async Task StartAsync(
        CancellationToken cancellationToken,
        bool ensureTrayHostOnStartup = false)
    {
        _ensureTrayHostOnStartup = ensureTrayHostOnStartup;
        _shutdownFrontendOnStop = true;
        await _logger.LogAsync("========== Backend start ==========", cancellationToken);
        await _logger.LogAsync("Backend starting.", cancellationToken);
        await _monitor.Catalog.LogConsistencySummaryAsync(cancellationToken);

        _monitor.ItemDetected += OnItemDetected;
        _pipeServer.BackendShutdownRequested += OnBackendShutdownRequested;
        _pipeServer.EnsureTrayHostRequested += OnEnsureTrayHostRequested;
        _monitor.Start(cancellationToken);
        _pipeServer.Start(cancellationToken);

        await _logger.LogAsync("Backend started.", cancellationToken);
        await _logger.LogAsync("========== Backend started ==========", cancellationToken);

        if (_ensureTrayHostOnStartup)
        {
            // Service startup performs one best-effort tray launch. Follow-up
            // attempts are then driven by session events and explicit pipe requests.
            TryEnsureTrayHost(null, requireAutostartPolicy: true);
        }
    }

    /// <summary>
    /// Stops async.
    /// </summary>
    public async Task StopAsync()
    {
        _monitor.ItemDetected -= OnItemDetected;
        _pipeServer.BackendShutdownRequested -= OnBackendShutdownRequested;
        _pipeServer.EnsureTrayHostRequested -= OnEnsureTrayHostRequested;
        if (_shutdownFrontendOnStop && !ConsumeKeepFrontendOnStopMarker())
        {
            // A normal backend shutdown should first ask the UI to exit cleanly
            // before the backend falls back to forcefully terminating it.
            await _pipeServer.BroadcastServiceStoppingAsync(CancellationToken.None);
            await _frontendAutostartLauncher.TryShutdownFrontendForActiveSessionAsync(null, CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(2));
            _frontendAutostartLauncher.KillFrontendProcessesForActiveSession(null);
        }

        _pipeServer.Stop();
        await _logger.LogAsync("========== Backend stop ==========");
        await _logger.LogAsync("Backend stopped.");
        await _logger.LogAsync("========== Backend stopped ==========");
    }

    private static bool ConsumeKeepFrontendOnStopMarker()
    {
        try
        {
            if (!File.Exists(KeepFrontendOnStopMarkerPath))
            {
                return false;
            }

            File.Delete(KeepFrontendOnStopMarkerPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
    }

    /// <summary>
    /// Executes notify Interactive Session Available.
    /// </summary>
    public void NotifyInteractiveSessionAvailable(int sessionId)
    {
        _monitor.Catalog.MarkInteractiveSessionObserved();
        _monitor.NotifyInteractiveSessionObserved();

        if (!_ensureTrayHostOnStartup)
        {
            return;
        }

        TryEnsureTrayHost(sessionId, requireAutostartPolicy: true);
    }

    private void OnConsoleCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _lifetimeCts?.Cancel();
    }

    private void OnItemDetected(object? sender, ContextMenuEntry item)
    {
        _ = HandleNewItemDetectedAsync(item);
    }

    private async Task HandleNewItemDetectedAsync(ContextMenuEntry item)
    {
        try
        {
            // Step 1: immediately block the brand-new menu item so it does not
            // remain active before the user reviews it.
            var quarantinedItem = await _monitor.Catalog.QuarantineNewItemAsync(item, CancellationToken.None);

            // Step 2: notify the tray/frontends once per logical item so the
            // same menu item appearing under multiple categories does not spam
            // duplicate approval notifications.
            if (ShouldBroadcastApprovalNotification(quarantinedItem))
            {
                _pipeServer.BroadcastNotification(
                    new BackendNotification
                    {
                        Kind = PipeNotificationKind.ItemDetected,
                        Item = quarantinedItem,
                        Message = $"A new context menu item was blocked pending approval: {quarantinedItem.DisplayName}",
                        Timestamp = DateTimeOffset.UtcNow
                    });
            }
        }
        catch (Exception ex)
        {
            await _logger.LogAsync($"Failed to quarantine new menu item {item.DisplayName}: {ex.Message}", CancellationToken.None);
        }
    }

    private void OnBackendShutdownRequested(object? sender, EventArgs e)
    {
        _shutdownFrontendOnStop = true;
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnEnsureTrayHostRequested(object? sender, EventArgs e)
    {
        _monitor.Catalog.MarkInteractiveSessionObserved();
        _monitor.NotifyInteractiveSessionObserved();
        TryEnsureTrayHost(null, requireAutostartPolicy: false);
    }

    private void TryEnsureTrayHost(int? sessionId, bool requireAutostartPolicy)
    {
        try
        {
            var launched = _frontendAutostartLauncher.TryLaunchTrayHostForActiveSession(sessionId, requireAutostartPolicy);
            _ = _logger.LogAsync(
                launched
                    ? "Requested tray-host startup for the active user session."
                    : requireAutostartPolicy
                        ? "Skipped tray-host startup because no eligible interactive user session was available or autostart policy is disabled."
                        : "Skipped tray-host startup because no eligible interactive user session was available.",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _ = _logger.LogAsync($"Failed to launch tray host from service: {ex.Message}", CancellationToken.None);
        }
    }

    private bool ShouldBroadcastApprovalNotification(ContextMenuEntry item)
    {
        var now = DateTimeOffset.UtcNow;
        var notificationKey = CreateApprovalNotificationKey(item);

        lock (_approvalNotificationSyncRoot)
        {
            // The dedup cache intentionally expires entries so the same logical item
            // can notify again later without growing this dictionary forever.
            var expiredKeys = _recentApprovalNotificationKeys
                .Where(static pair => pair.Value <= DateTimeOffset.UtcNow)
                .Select(static pair => pair.Key)
                .ToArray();

            foreach (var expiredKey in expiredKeys)
            {
                _recentApprovalNotificationKeys.Remove(expiredKey);
            }

            if (_recentApprovalNotificationKeys.TryGetValue(notificationKey, out var expiresAtUtc)
                && expiresAtUtc > now)
            {
                return false;
            }

            _recentApprovalNotificationKeys[notificationKey] = now.Add(ApprovalNotificationDedupWindow);
            return true;
        }
    }

    private static string CreateApprovalNotificationKey(ContextMenuEntry item)
    {
        return ContextMenuApprovalIdentity.CreateLogicalItemKey(item);
    }

}
