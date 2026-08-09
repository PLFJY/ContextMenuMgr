using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Principal;

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
    private readonly ConcurrentDictionary<string, byte> _quarantineInProgress = new(StringComparer.OrdinalIgnoreCase);
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
        BackendEmergencyLogger.Log("CreateDefault: TryMigrateLegacyRuntimeFiles started.");
        TryMigrateLegacyRuntimeFiles();
        BackendEmergencyLogger.Log("CreateDefault: TryMigrateLegacyRuntimeFiles completed.");

        BackendEmergencyLogger.Log("CreateDefault: RuntimeDataAclRepair started.");
        var aclRepairResult = RuntimeDataAclRepairService.RepairRuntimeDataDirectory(RuntimePaths.RootDirectory);
        BackendEmergencyLogger.Log($"CreateDefault: RuntimeDataAclRepair completed. Success={aclRepairResult.Success}, Code={aclRepairResult.Code}, Detail={aclRepairResult.Detail}");

        BackendEmergencyLogger.Log("CreateDefault: creating FileLogger.");
        var logger = new FileLogger(RuntimePaths.BackendLogPath);
        BackendEmergencyLogger.Log("CreateDefault: FileLogger created.");
        logger.LogFireAndForget(
            aclRepairResult.Success ? RuntimeLogLevel.Information : RuntimeLogLevel.Warning,
            $"RuntimeDataAclRepairStartup: Success={aclRepairResult.Success}, Code={aclRepairResult.Code}, Detail={aclRepairResult.Detail}");
        logger.LogFireAndForget(
            $"RuntimePackage: PackageKind={RuntimePaths.PackageKind}, RootDirectory={RuntimePaths.RootDirectory}, LogsDirectory={RuntimePaths.LogsDirectory}, DataDirectory={RuntimePaths.DataDirectory}");

        BackendEmergencyLogger.Log("CreateDefault: creating RuntimeHostIdentityProvider.");
        var hostIdentityProvider = new RuntimeHostIdentityProvider(logger);
        BackendEmergencyLogger.Log("CreateDefault: RuntimeHostIdentityProvider created.");

        BackendEmergencyLogger.Log("CreateDefault: creating ContextMenuStateStore.");
        var stateStore = new ContextMenuStateStore(RuntimePaths.StateDatabasePath, logger, hostIdentityProvider);
        BackendEmergencyLogger.Log("CreateDefault: ContextMenuStateStore created.");

        BackendEmergencyLogger.Log("CreateDefault: creating BackendProtectionSettingsStore.");
        var protectionSettingsStore = new BackendProtectionSettingsStore(RuntimePaths.BackendProtectionSettingsPath, logger);
        BackendEmergencyLogger.Log("CreateDefault: BackendProtectionSettingsStore created.");

        BackendEmergencyLogger.Log("CreateDefault: creating RegistryBackupService.");
        var backupService = new RegistryBackupService(RuntimePaths.DeletedBackupsDirectory, logger, hostIdentityProvider);
        BackendEmergencyLogger.Log("CreateDefault: RegistryBackupService created.");

        BackendEmergencyLogger.Log("CreateDefault: creating ContextMenuRegistryCatalog.");
        var catalog = new ContextMenuRegistryCatalog(logger, stateStore, backupService, protectionSettingsStore);
        BackendEmergencyLogger.Log("CreateDefault: ContextMenuRegistryCatalog created.");

        BackendEmergencyLogger.Log("CreateDefault: creating SpecialMenuService.");
        var specialMenuService = new SpecialMenuService(logger);
        BackendEmergencyLogger.Log("CreateDefault: SpecialMenuService created.");

        BackendEmergencyLogger.Log("CreateDefault: creating Windows11BlocksService.");
        var windows11BlocksService = new Windows11BlocksService(logger);
        BackendEmergencyLogger.Log("CreateDefault: Windows11BlocksService created.");

        BackendEmergencyLogger.Log("CreateDefault: creating Win11ClassicContextMenuService.");
        var win11ClassicContextMenuService = new Win11ClassicContextMenuService(logger);
        BackendEmergencyLogger.Log("CreateDefault: Win11ClassicContextMenuService created.");

        BackendEmergencyLogger.Log("CreateDefault: creating AutoStartService.");
        var autoStartService = new AutoStartService(logger);
        BackendEmergencyLogger.Log("CreateDefault: AutoStartService created.");

        BackendEmergencyLogger.Log("CreateDefault: creating FileTypeSceneMenuService.");
        var fileTypeSceneMenuService = new FileTypeSceneMenuService(catalog, stateStore, logger);
        BackendEmergencyLogger.Log("CreateDefault: FileTypeSceneMenuService created.");

        BackendEmergencyLogger.Log("CreateDefault: creating ExplorerRestartService.");
        var explorerRestartService = new ExplorerRestartService();
        BackendEmergencyLogger.Log("CreateDefault: ExplorerRestartService created.");

        BackendEmergencyLogger.Log("CreateDefault: creating ContextMenuRegistryMonitor.");
        var userContextResolver = new BackendUserContextResolver(logger);
        var monitor = new ContextMenuRegistryMonitor(catalog, logger, userContextResolver);
        BackendEmergencyLogger.Log("CreateDefault: ContextMenuRegistryMonitor created.");

        BackendEmergencyLogger.Log("CreateDefault: creating NamedPipeBackendServer.");
        var pipeServer = new NamedPipeBackendServer(catalog, specialMenuService, windows11BlocksService, win11ClassicContextMenuService, autoStartService, fileTypeSceneMenuService, explorerRestartService, logger);
        BackendEmergencyLogger.Log("CreateDefault: NamedPipeBackendServer created.");
        stateStore.PortableHostMismatchDetected += (_, message) =>
        {
            pipeServer.BroadcastNotification(
                new BackendNotification
                {
                    Kind = PipeNotificationKind.ServiceMessage,
                    Message = message,
                    Timestamp = DateTimeOffset.UtcNow
                });
        };

        BackendEmergencyLogger.Log("CreateDefault: creating FrontendAutostartLauncher.");
        var frontendAutostartLauncher = new FrontendAutostartLauncher(AppContext.BaseDirectory);
        BackendEmergencyLogger.Log("CreateDefault: FrontendAutostartLauncher created.");

        return new BackendRuntime(logger, monitor, pipeServer, frontendAutostartLauncher);
    }

    public Task LogServiceStartupFailureAsync(Exception exception, bool cancellationRequested)
    {
        var identity = TryGetCurrentIdentityName();
        return _logger.LogAsync(
            RuntimeLogLevel.Error,
            $"Windows service runtime startup failed. Identity={identity}, CancellationRequested={cancellationRequested}.{Environment.NewLine}{exception}",
            CancellationToken.None);
    }

    private static void TryMigrateLegacyRuntimeFiles()
    {
        TryCopyIfMissing(RuntimePaths.LegacyProgramDataStateDatabasePath, RuntimePaths.StateDatabasePath);
        TryCopyIfMissing(RuntimePaths.LegacyStateDatabasePath, RuntimePaths.StateDatabasePath);
        TryCopyIfMissing(RuntimePaths.LegacyProgramDataBackendProtectionSettingsPath, RuntimePaths.BackendProtectionSettingsPath);
        TryCopyIfMissing(RuntimePaths.LegacyBackendProtectionSettingsPath, RuntimePaths.BackendProtectionSettingsPath);

        if (RuntimePaths.PackageKind == RuntimePackageKind.Portable)
        {
            TryCopyDirectoryContentsIfMissing(RuntimePaths.LegacyProgramDataDeletedBackupsDirectory, RuntimePaths.DeletedBackupsDirectory);
            TryCopyDirectoryContentsIfMissing(RuntimePaths.LegacyProgramDataLogsDirectory, RuntimePaths.LogsDirectory);
            TryCopyDirectoryContentsIfMissing(RuntimePaths.LegacyProgramDataGeneratedProgramsDirectory, RuntimePaths.GeneratedProgramsDirectory);
        }
    }

    private static string TryGetCurrentIdentityName()
    {
        try
        {
            return WindowsIdentity.GetCurrent().Name;
        }
        catch (Exception ex)
        {
            return $"<unavailable:{ex.GetType().Name}>";
        }
    }

    private static void TryCopyIfMissing(string sourcePath, string destinationPath)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase)
                || !File.Exists(sourcePath)
                || File.Exists(destinationPath))
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

    private static void TryCopyDirectoryContentsIfMissing(string sourceDirectory, string destinationDirectory)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(sourceDirectory), Path.GetFullPath(destinationDirectory), StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(sourceDirectory))
            {
                return;
            }

            foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                var destinationPath = Path.Combine(destinationDirectory, relativePath);
                if (File.Exists(destinationPath))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourcePath, destinationPath, overwrite: false);
            }
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
        BackendEmergencyLogger.Log($"StartAsync entered. EnsureTrayHostOnStartup={ensureTrayHostOnStartup}, CancellationRequested={cancellationToken.IsCancellationRequested}.");
        _ensureTrayHostOnStartup = ensureTrayHostOnStartup;
        _shutdownFrontendOnStop = true;
        var stage = "Initialize";

        try
        {
            stage = "FileLogger";
            BackendEmergencyLogger.Log("BackendStartStage=FileLogger started.");
            await _logger.LogAsync("========== Backend start ==========", cancellationToken);
            await _logger.LogAsync("BackendStartStage=FileLogger started.", cancellationToken);
            await _logger.LogAsync($"BackendStartStage=FileLogger completed. CurrentLevel={_logger.CurrentLevel}.", cancellationToken);
            BackendEmergencyLogger.Log($"BackendStartStage=FileLogger completed. CurrentLevel={_logger.CurrentLevel}.");

            stage = "BackendStarting";
            await _logger.LogAsync("BackendStartStage=BackendStarting started.", cancellationToken);
            BackendEmergencyLogger.Log("BackendStartStage=BackendStarting started.");
            await _logger.LogAsync("Backend starting.", cancellationToken);
            BackendEmergencyLogger.Log("Backend starting.");
            await _logger.LogAsync("BackendStartStage=BackendStarting completed.", cancellationToken);
            BackendEmergencyLogger.Log("BackendStartStage=BackendStarting completed.");

            stage = "MonitorEventSubscription";
            await _logger.LogAsync("BackendStartStage=MonitorEventSubscription started.", cancellationToken);
            BackendEmergencyLogger.Log("BackendStartStage=MonitorEventSubscription started.");
            _monitor.ItemDetected += OnItemDetected;
            _pipeServer.BackendShutdownRequested += OnBackendShutdownRequested;
            _pipeServer.EnsureTrayHostRequested += OnEnsureTrayHostRequested;
            await _logger.LogAsync("BackendStartStage=MonitorEventSubscription completed.", cancellationToken);
            BackendEmergencyLogger.Log("BackendStartStage=MonitorEventSubscription completed.");

            stage = "PipeServerStart";
            await _logger.LogAsync("BackendStartStage=PipeServerStart started.", cancellationToken);
            BackendEmergencyLogger.Log("BackendStartStage=PipeServerStart started.");
            _pipeServer.Start(cancellationToken);
            await _logger.LogAsync("BackendStartStage=PipeServerStart completed.", cancellationToken);
            BackendEmergencyLogger.Log("BackendStartStage=PipeServerStart completed.");

            stage = "BackendStarted";
            await _logger.LogAsync("Backend started.", cancellationToken);
            await _logger.LogAsync("BackendStartStage=BackendStarted completed.", cancellationToken);
            await _logger.LogAsync("========== Backend started ==========", cancellationToken);
            BackendEmergencyLogger.Log("Backend started.");
            BackendEmergencyLogger.Log("BackendStartStage=BackendStarted completed.");

            stage = "MonitorStart";
            await _logger.LogAsync("BackendStartStage=MonitorStart started.", cancellationToken);
            BackendEmergencyLogger.Log("BackendStartStage=MonitorStart started.");
            _monitor.Start(cancellationToken);
            await _logger.LogAsync("BackendStartStage=MonitorStart completed.", cancellationToken);
            BackendEmergencyLogger.Log("BackendStartStage=MonitorStart completed.");

            stage = "StartupDiagnosticsSchedule";
            await _logger.LogAsync("BackendStartStage=StartupDiagnostics scheduled.", cancellationToken);
            BackendEmergencyLogger.Log("BackendStartStage=StartupDiagnostics scheduled.");
            _ = Task.Run(
                () => RunStartupDiagnosticsAsync(cancellationToken),
                CancellationToken.None);

            if (_ensureTrayHostOnStartup)
            {
                stage = "TryEnsureTrayHost";
                await _logger.LogAsync("BackendStartStage=TryEnsureTrayHost started.", cancellationToken);
                BackendEmergencyLogger.Log("BackendStartStage=TryEnsureTrayHost started.");
                // Service startup performs one best-effort tray launch. Follow-up
                // attempts are then driven by session events and explicit pipe requests.
                TryEnsureTrayHost(null, requireAutostartPolicy: true);
                await _logger.LogAsync("BackendStartStage=TryEnsureTrayHost completed.", cancellationToken);
                BackendEmergencyLogger.Log("BackendStartStage=TryEnsureTrayHost completed.");
            }
        }
        catch (Exception ex)
        {
            try
            {
                await _logger.LogAsync(
                    RuntimeLogLevel.Error,
                    $"BackendStartStage={stage} failed: {ex}",
                    CancellationToken.None);
            }
            catch
            {
            }

            BackendEmergencyLogger.Log(ex, $"BackendStartStage={stage} failed.");
            throw;
        }
    }

    private async Task RunStartupDiagnosticsAsync(CancellationToken serviceCancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            await _logger.LogAsync("BackendStartStage=StartupDiagnostics started.", CancellationToken.None);
            BackendEmergencyLogger.Log("BackendStartStage=StartupDiagnostics started.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(serviceCancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            await LogConsistencySummaryForStartupAsync(timeoutCts.Token);

            var elapsed = DateTimeOffset.UtcNow - startedAt;
            await _logger.LogAsync(
                $"BackendStartStage=StartupDiagnostics completed. ElapsedMs={elapsed.TotalMilliseconds:0}.",
                CancellationToken.None);
            BackendEmergencyLogger.Log($"BackendStartStage=StartupDiagnostics completed. ElapsedMs={elapsed.TotalMilliseconds:0}.");
        }
        catch (OperationCanceledException) when (!serviceCancellationToken.IsCancellationRequested)
        {
            try
            {
                await _logger.LogAsync(
                    RuntimeLogLevel.Warning,
                    "BackendStartStage=StartupDiagnostics timed out; backend remains running.",
                    CancellationToken.None);
            }
            catch
            {
            }

            BackendEmergencyLogger.Log("BackendStartStage=StartupDiagnostics timed out; backend remains running.");
        }
        catch (OperationCanceledException)
        {
            try
            {
                await _logger.LogAsync(
                    RuntimeLogLevel.Warning,
                    "BackendStartStage=StartupDiagnostics canceled because backend service is stopping.",
                    CancellationToken.None);
            }
            catch
            {
            }

            BackendEmergencyLogger.Log("BackendStartStage=StartupDiagnostics canceled because backend service is stopping.");
        }
        catch (Exception ex)
        {
            try
            {
                await _logger.LogAsync(
                    RuntimeLogLevel.Warning,
                    "BackendStartStage=StartupDiagnostics failed but backend remains running: " + ex,
                    CancellationToken.None);
            }
            catch
            {
            }

            BackendEmergencyLogger.Log(ex, "BackendStartStage=StartupDiagnostics failed but backend remains running.");
        }
    }

    private async Task LogConsistencySummaryForStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _logger.LogAsync("BackendStartStage=LogConsistencySummary started.", cancellationToken);
            BackendEmergencyLogger.Log("BackendStartStage=LogConsistencySummary started.");
            await _monitor.Catalog.LogConsistencySummaryAsync(cancellationToken);
            await _logger.LogAsync("BackendStartStage=LogConsistencySummary completed.", cancellationToken);
            BackendEmergencyLogger.Log("BackendStartStage=LogConsistencySummary completed.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _logger.LogAsync(
                    RuntimeLogLevel.Warning,
                    "LogConsistencySummary failed but startup will continue: " + ex,
                    CancellationToken.None);
            }
            catch
            {
            }

            BackendEmergencyLogger.Log(ex, "LogConsistencySummary failed but startup will continue.");
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

        var keepFrontendMarkerConsumed = ConsumeKeepFrontendOnStopMarker();
        var willShutdownFrontend = _shutdownFrontendOnStop && !keepFrontendMarkerConsumed;
        await _logger.LogAsync(
            $"BackendStopFrontendShutdownPolicy: ShutdownFrontendOnStop={_shutdownFrontendOnStop}, KeepFrontendMarkerConsumed={keepFrontendMarkerConsumed}, WillShutdownFrontend={willShutdownFrontend}.",
            CancellationToken.None);
        BackendEmergencyLogger.Log($"BackendStopFrontendShutdownPolicy: ShutdownFrontendOnStop={_shutdownFrontendOnStop}, KeepFrontendMarkerConsumed={keepFrontendMarkerConsumed}, WillShutdownFrontend={willShutdownFrontend}.");

        if (willShutdownFrontend)
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

    public void SuppressFrontendShutdownOnStop(string reason)
    {
        _shutdownFrontendOnStop = false;

        try
        {
            _ = _logger.LogAsync(
                RuntimeLogLevel.Warning,
                $"Frontend shutdown on backend stop suppressed. Reason={reason}",
                CancellationToken.None);
        }
        catch
        {
        }

        BackendEmergencyLogger.Log($"Frontend shutdown on backend stop suppressed. Reason={reason}");
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
        if (!_quarantineInProgress.TryAdd(item.Id, 0))
        {
            return;
        }

        _ = HandleNewItemDetectedAsync(item);
    }

    private async Task HandleNewItemDetectedAsync(ContextMenuEntry item)
    {
        try
        {
            // Select the quarantine path based on the detected change kind.
            // Reappeared items (previously deleted, then recreated) use a
            // dedicated path that preserves deletion provenance.
            ContextMenuEntry quarantinedItem;
            string notificationMessage;

            if (item.DetectedChangeKind == ContextMenuChangeKind.Reappeared)
            {
                quarantinedItem = await _monitor.Catalog.QuarantineReappearedItemAsync(item, CancellationToken.None);
                notificationMessage = $"A previously deleted context menu item has reappeared and was blocked pending approval: {quarantinedItem.DisplayName}";
            }
            else
            {
                quarantinedItem = await _monitor.Catalog.QuarantineNewItemAsync(item, CancellationToken.None);
                notificationMessage = $"A new context menu item was blocked pending approval: {quarantinedItem.DisplayName}";
            }

            // Notify the tray/frontends once per logical item so the
            // same menu item appearing under multiple categories does not spam
            // duplicate approval notifications.
            if (ShouldBroadcastApprovalNotification(quarantinedItem))
            {
                _pipeServer.BroadcastNotification(
                    new BackendNotification
                    {
                        Kind = PipeNotificationKind.ItemDetected,
                        Item = quarantinedItem,
                        Message = notificationMessage,
                        Timestamp = DateTimeOffset.UtcNow
                    });
            }
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Error, $"Failed to quarantine detected menu item {item.DisplayName}: {ex.Message}", CancellationToken.None);
        }
        finally
        {
            _quarantineInProgress.TryRemove(item.Id, out _);
        }
    }

    private void OnBackendShutdownRequested(object? sender, EventArgs e)
    {
        _shutdownFrontendOnStop = true;
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnEnsureTrayHostRequested(object? sender, EventArgs e)
    {
        // Ensuring the tray host is only a UI/session-side operation. It must not
        // reset the registry monitor baseline, otherwise a runtime-added menu item
        // could be mistaken for a startup/offline change and avoid quarantine.
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
            _ = _logger.LogAsync(RuntimeLogLevel.Error, $"Failed to launch tray host from service: {ex.Message}", CancellationToken.None);
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
