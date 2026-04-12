using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Hosting;

public sealed class BackendRuntime : IDisposable
{
    private readonly FileLogger _logger;
    private readonly ContextMenuRegistryMonitor _monitor;
    private readonly NamedPipeBackendServer _pipeServer;
    private readonly FrontendAutostartLauncher _frontendAutostartLauncher;
    private bool _stopWhenFrontendDisconnected = true;
    private bool _launchFrontendOnStartup;
    private bool _hasSeenFrontendSubscriber;
    private CancellationTokenSource? _lifetimeCts;

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

    public static BackendRuntime CreateDefault()
    {
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ContextMenuMgr");

        var logger = new FileLogger(Path.Combine(dataRoot, "Logs", "backend.log"));
        var stateStore = new ContextMenuStateStore(Path.Combine(dataRoot, "Data", "context-menu-state.json"));
        var protectionSettingsStore = new BackendProtectionSettingsStore(Path.Combine(dataRoot, "Data", "backend-protection-settings.json"));
        var backupService = new RegistryBackupService(Path.Combine(dataRoot, "DeletedBackups"));
        var catalog = new ContextMenuRegistryCatalog(logger, stateStore, backupService, protectionSettingsStore);
        var monitor = new ContextMenuRegistryMonitor(catalog, logger);
        var pipeServer = new NamedPipeBackendServer(catalog, logger);
        var frontendAutostartLauncher = new FrontendAutostartLauncher(AppContext.BaseDirectory);

        return new BackendRuntime(logger, monitor, pipeServer, frontendAutostartLauncher);
    }

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

    public async Task StartAsync(
        CancellationToken cancellationToken,
        bool stopWhenFrontendDisconnected = true,
        bool launchFrontendOnStartup = false)
    {
        _stopWhenFrontendDisconnected = stopWhenFrontendDisconnected;
        _launchFrontendOnStartup = launchFrontendOnStartup;
        _hasSeenFrontendSubscriber = false;
        await _logger.LogAsync("Backend starting.", cancellationToken);
        await _monitor.Catalog.LogConsistencySummaryAsync(cancellationToken);

        _monitor.ItemDetected += OnItemDetected;
        _pipeServer.FrontendPresenceTimedOut += OnFrontendPresenceTimedOut;
        _pipeServer.NotificationSubscriberConnected += OnNotificationSubscriberConnected;
        _monitor.Start(cancellationToken);
        _pipeServer.Start(cancellationToken);

        await _logger.LogAsync("Backend started.", cancellationToken);

        if (_launchFrontendOnStartup)
        {
            TryLaunchFrontend(null);
        }
    }

    public async Task StopAsync()
    {
        _monitor.ItemDetected -= OnItemDetected;
        _pipeServer.FrontendPresenceTimedOut -= OnFrontendPresenceTimedOut;
        _pipeServer.NotificationSubscriberConnected -= OnNotificationSubscriberConnected;
        _pipeServer.Stop();
        await _logger.LogAsync("Backend stopped.");
    }

    public void NotifyInteractiveSessionAvailable(int sessionId)
    {
        if (!_launchFrontendOnStartup)
        {
            return;
        }

        TryLaunchFrontend(sessionId);
    }

    public void Dispose()
    {
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
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

            // Step 2: notify the unprivileged frontend that a decision is needed.
            _pipeServer.BroadcastNotification(
                new BackendNotification
                {
                    Kind = PipeNotificationKind.ItemDetected,
                    Item = quarantinedItem,
                    Message = $"A new context menu item was blocked pending approval: {quarantinedItem.DisplayName}",
                    Timestamp = DateTimeOffset.UtcNow
                });
        }
        catch (Exception ex)
        {
            await _logger.LogAsync($"Failed to quarantine new menu item {item.DisplayName}: {ex.Message}", CancellationToken.None);
        }
    }

    private async void OnFrontendPresenceTimedOut(object? sender, EventArgs e)
    {
        if (!_stopWhenFrontendDisconnected)
        {
            return;
        }

        if (_launchFrontendOnStartup && !_hasSeenFrontendSubscriber)
        {
            await _logger.LogAsync("Frontend timeout ignored because no frontend subscriber has connected yet.", CancellationToken.None);
            return;
        }

        await _logger.LogAsync("Frontend process is no longer connected. Requesting backend stop.", CancellationToken.None);
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnNotificationSubscriberConnected(object? sender, EventArgs e)
    {
        _hasSeenFrontendSubscriber = true;
    }

    private void TryLaunchFrontend(int? sessionId)
    {
        try
        {
            var launched = _frontendAutostartLauncher.TryLaunchFrontendForActiveSession(sessionId);
            _ = _logger.LogAsync(
                launched
                    ? "Requested tray frontend startup for the active user session."
                    : "Skipped tray frontend startup because no eligible interactive user session was available.",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _ = _logger.LogAsync($"Failed to launch tray frontend from service: {ex.Message}", CancellationToken.None);
        }
    }
}
