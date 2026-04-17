using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using System.IO;

namespace ContextMenuMgr.Backend.Hosting;

public sealed class BackendRuntime : IDisposable
{
    private readonly FileLogger _logger;
    private readonly ContextMenuRegistryMonitor _monitor;
    private readonly NamedPipeBackendServer _pipeServer;
    private readonly FrontendAutostartLauncher _frontendAutostartLauncher;
    private bool _ensureTrayHostOnStartup;
    private bool _shutdownFrontendOnStop = true;
    private CancellationTokenSource? _lifetimeCts;
    private static readonly string KeepFrontendOnStopMarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ContextMenuMgr",
        "Data",
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
        bool ensureTrayHostOnStartup = false)
    {
        _ensureTrayHostOnStartup = ensureTrayHostOnStartup;
        _shutdownFrontendOnStop = true;
        await _logger.LogAsync("Backend starting.", cancellationToken);
        await _monitor.Catalog.LogConsistencySummaryAsync(cancellationToken);

        _monitor.ItemDetected += OnItemDetected;
        _pipeServer.BackendShutdownRequested += OnBackendShutdownRequested;
        _pipeServer.EnsureTrayHostRequested += OnEnsureTrayHostRequested;
        _monitor.Start(cancellationToken);
        _pipeServer.Start(cancellationToken);

        await _logger.LogAsync("Backend started.", cancellationToken);

        if (_ensureTrayHostOnStartup)
        {
            TryEnsureTrayHost(null, requireAutostartPolicy: false);
        }
    }

    public async Task StopAsync()
    {
        _monitor.ItemDetected -= OnItemDetected;
        _pipeServer.BackendShutdownRequested -= OnBackendShutdownRequested;
        _pipeServer.EnsureTrayHostRequested -= OnEnsureTrayHostRequested;
        if (_shutdownFrontendOnStop && !ConsumeKeepFrontendOnStopMarker())
        {
            await _pipeServer.BroadcastServiceStoppingAsync(CancellationToken.None);
            await _frontendAutostartLauncher.TryShutdownFrontendForActiveSessionAsync(null, CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(2));
            _frontendAutostartLauncher.KillFrontendProcessesForActiveSession(null);
        }
        _pipeServer.Stop();
        await _logger.LogAsync("Backend stopped.");
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

    public void Dispose()
    {
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
    }

    public void NotifyInteractiveSessionAvailable(int sessionId)
    {
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

    private void OnBackendShutdownRequested(object? sender, EventArgs e)
    {
        _shutdownFrontendOnStop = true;
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnEnsureTrayHostRequested(object? sender, EventArgs e)
    {
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

}
