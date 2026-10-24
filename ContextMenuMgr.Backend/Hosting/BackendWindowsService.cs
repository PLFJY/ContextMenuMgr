using System.ServiceProcess;
using System.Diagnostics;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Hosting;

// The backend executable can run interactively for development, or as a
// Windows Service when installed by the frontend bootstrapper.
/// <summary>
/// Represents the backend Windows Service.
/// </summary>
public sealed class BackendWindowsService : ServiceBase
{
    private readonly BackendRuntime _runtime;
    private readonly BackendServiceTerminationState _terminationState = new();
    private readonly string _stopReasonMarkerPath = Path.Combine(
        RuntimePaths.DataDirectory,
        ServiceMetadata.StopReasonMarkerFileName);
    private CancellationTokenSource? _serviceCts;
    private readonly ServiceStopSequence _stopSequence = new();
    private PortableRuntimeIdentity? _portableIdentity;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackendWindowsService"/> class.
    /// </summary>
    public BackendWindowsService(BackendRuntime runtime)
    {
        _runtime = runtime;
        ServiceName = ServiceMetadata.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
        CanHandleSessionChangeEvent = true;
    }

    /// <summary>
    /// Executes should Run As Service.
    /// </summary>
    public static bool ShouldRunAsService(string[] args) =>
        args.Any(static arg => string.Equals(arg, "--service", StringComparison.OrdinalIgnoreCase)) ||
        !Environment.UserInteractive;

    protected override void OnStart(string[] args)
    {
        if (RuntimePaths.PackageKind == RuntimePackageKind.Portable)
        {
            var files = new PortableRuntimeIdentity();
            try
            {
                files.Pin(Environment.ProcessPath ?? throw new IOException("Backend image unavailable."));
                files.Pin(Path.Combine(AppContext.BaseDirectory, RuntimePackageManifest.FileName));
                files.Pin(Path.Combine(AppContext.BaseDirectory, "ContextMenuManagerPlus.exe"));
                _portableIdentity = files;
            }
            catch (Exception ex)
            {
                files.Dispose();
                BackendEmergencyLogger.Log(ex, "PortableRuntimeOwnershipUncertain: automatic removal disabled for this runtime.");
            }
        }
        BackendEmergencyLogger.Log($"OnStart entered. Args={string.Join(' ', args)}, PID={Environment.ProcessId}, ServiceName={ServiceName}.");
        _serviceCts = new CancellationTokenSource();
        _runtime.StopRequested += OnRuntimeStopRequested;
        _runtime.LogServiceLifecycleDiagnostics();
        BackendEmergencyLogger.Log("OnStart: scheduling StartRuntimeAsync.");
        var startupTask = StartRuntimeAsync(_serviceCts.Token);
        _ = startupTask;
        BackendEmergencyLogger.Log("OnStart: StartRuntimeAsync scheduled.");
    }

    protected override void OnStop()
    {
        _terminationState.MarkServiceControlManagerStop(ServiceStopReasonMarker.Read(_stopReasonMarkerPath));
        _stopSequence.Stop(null, () => StopCore("OnStop"));
    }

    protected override void OnShutdown()
    {
        _terminationState.MarkIntentionalStop(BackendServiceStopReason.WindowsShutdown);
        _stopSequence.Stop(
            () => AttemptPortableCleanup(windowsShutdown: true, caller: null),
            () => StopCore("OnShutdown"));
        base.OnShutdown();
    }

    private void StopCore(string callbackName)
    {
        ExitCode = _terminationState.ExitCode;
        BackendEmergencyLogger.Log($"{callbackName} entered. ServiceName={ServiceName}, PID={Environment.ProcessId}, StopReason={_terminationState.StopReason}, ExitCode={ExitCode}.");
        try
        {
            if (RuntimePaths.PackageKind == RuntimePackageKind.Portable) _runtime.PrepareForStop();
            _runtime.StopRequested -= OnRuntimeStopRequested;
            _serviceCts?.Cancel();
            BackendEmergencyLogger.Log($"{callbackName}: cancellation requested. StopReason={_terminationState.StopReason}.");
            BackendEmergencyLogger.Log($"{callbackName}: StopAsync started. StopReason={_terminationState.StopReason}.");
            _runtime.StopAsync().GetAwaiter().GetResult();
            BackendEmergencyLogger.Log($"{callbackName}: StopAsync completed. StopReason={_terminationState.StopReason}, ExitCode={ExitCode}.");
        }
        catch (Exception ex)
        {
            BackendEmergencyLogger.Log(ex, $"{callbackName} failed. StopReason={_terminationState.StopReason}, ExitCode={ExitCode}.");
            throw;
        }
        finally
        {
            _portableIdentity?.Dispose();
            _portableIdentity = null;
            _serviceCts?.Dispose();
            _serviceCts = null;
            ServiceStopReasonMarker.Delete(_stopReasonMarkerPath);
        }
    }

    private void OnRuntimeStopRequested(object? sender, BackendStopRequestedEventArgs e)
    {
        _terminationState.MarkIntentionalStop(e.Reason);
        _stopSequence.Request(
            e.PortableCaller is { } caller
                ? () => AttemptPortableCleanup(windowsShutdown: false, caller)
                : null,
            () => _ = Task.Run(() =>
        {
            try
            {
                Stop();
            }
            catch (Exception ex)
            {
                BackendEmergencyLogger.Log(ex, "Runtime requested service stop, but Stop() failed.");
            }
        }));
    }

    private void AttemptPortableCleanup(bool windowsShutdown, PortableExitCaller? caller)
    {
        if (RuntimePaths.PackageKind != RuntimePackageKind.Portable) return;
        if (_portableIdentity is null)
        {
            BackendEmergencyLogger.Log("PortableServiceCleanup: Result=StartupOwnershipUncertain, continuing normal stop.");
            return;
        }
        if (_runtime.PrepareForStop()) PortableServiceCleanup.Attempt(windowsShutdown, caller);
        else BackendEmergencyLogger.Log("PortableServiceCleanup: Result=ActivationInProgress, continuing normal stop.");
    }

    private async Task StartRuntimeAsync(CancellationToken cancellationToken)
    {
        BackendEmergencyLogger.Log($"StartRuntimeAsync entered. CancellationRequested={cancellationToken.IsCancellationRequested}.");
        try
        {
            // Keep the SCM startup path fast. The frontend/bootstrapper performs
            // the stricter "wait until pipe is ready" check separately.
            await _runtime.StartAsync(cancellationToken, ensureTrayHostOnStartup: true);
            BackendEmergencyLogger.Log("StartRuntimeAsync completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            BackendEmergencyLogger.Log($"StartRuntimeAsync canceled during intentional stop. StopReason={_terminationState.StopReason}.");
        }
        catch (Exception ex)
        {
            _terminationState.MarkStartupFailure();
            ExitCode = _terminationState.ExitCode;
            BackendEmergencyLogger.Log(ex, "Windows service runtime startup failed.");
            BackendEmergencyLogger.Log($"StopReason={_terminationState.StopReason}, ExitCode={ExitCode}, RecoveryEligible=True.");
            BackendEmergencyLogger.Log("ServiceStartupFailedSuppressingFrontendShutdown.");
            try
            {
                _runtime.SuppressFrontendShutdownOnStop(
                    $"Windows service runtime startup failed. CancellationRequested={cancellationToken.IsCancellationRequested}");
            }
            catch (Exception suppressException)
            {
                BackendEmergencyLogger.Log(
                    suppressException,
                    "Failed to suppress frontend shutdown after service startup failure.");
            }

            try
            {
                await _runtime.LogServiceStartupFailureAsync(ex, cancellationToken.IsCancellationRequested);
            }
            catch (Exception logException)
            {
                BackendEmergencyLogger.Log(logException, "FileLogger service startup failure logging failed.");
            }

            TryWriteStartupFailureEventLog(ex, cancellationToken.IsCancellationRequested);

            try
            {
                BackendEmergencyLogger.Log("StopAfterStartupFailureRequested.");
                Stop();
            }
            catch (Exception stopException)
            {
                BackendEmergencyLogger.Log(stopException, "Stop() after Windows service runtime startup failure failed.");
            }
        }
    }

    internal BackendServiceStopReason StopReason => _terminationState.StopReason;

    internal int ProcessExitCode
    {
        get
        {
            _terminationState.MarkUnexpectedRunReturn();
            ExitCode = _terminationState.ExitCode;
            return _terminationState.ExitCode;
        }
    }

    private void TryWriteStartupFailureEventLog(Exception exception, bool cancellationRequested)
    {
        try
        {
            EventLog.WriteEntry(
                ServiceName,
                $"Windows service runtime startup failed. CancellationRequested={cancellationRequested}.{Environment.NewLine}{exception}",
                EventLogEntryType.Error);
        }
        catch
        {
        }
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        base.OnSessionChange(changeDescription);

        if (changeDescription.Reason is SessionChangeReason.SessionLogon
            or SessionChangeReason.SessionUnlock
            or SessionChangeReason.ConsoleConnect
            or SessionChangeReason.RemoteConnect)
        {
            _runtime.NotifyInteractiveSessionAvailable(changeDescription.SessionId);
        }
    }

}
