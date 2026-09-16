namespace ContextMenuMgr.Backend.Hosting;

internal enum BackendServiceStopReason
{
    Unknown,
    FrontendRequest,
    ServiceControlManager,
    WindowsShutdown,
    StartupFailure,
    ForceRepair,
    Uninstall,
    ExplicitServiceStop,
    InstallRepair,
    UnexpectedProcessExit
}

internal sealed class BackendStopRequestedEventArgs(BackendServiceStopReason reason) : EventArgs
{
    public BackendServiceStopReason Reason { get; } = reason;
}

internal sealed class BackendServiceTerminationState
{
    // ERROR_EXCEPTION_IN_SERVICE is a concrete SCM-visible failure code and
    // does not require a separate service-specific exit code.
    internal const int StartupFailureExitCode = 1064;

    private readonly Lock _syncRoot = new();
    private BackendServiceStopReason _stopReason = BackendServiceStopReason.Unknown;
    private int _exitCode;

    public BackendServiceStopReason StopReason
    {
        get
        {
            lock (_syncRoot)
            {
                return _stopReason;
            }
        }
    }

    public int ExitCode
    {
        get
        {
            lock (_syncRoot)
            {
                return _exitCode;
            }
        }
    }

    public void MarkIntentionalStop(BackendServiceStopReason reason)
    {
        if (reason is BackendServiceStopReason.Unknown
            or BackendServiceStopReason.StartupFailure
            or BackendServiceStopReason.UnexpectedProcessExit)
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "An intentional stop requires a concrete non-failure reason.");
        }

        lock (_syncRoot)
        {
            if (_stopReason is BackendServiceStopReason.Unknown or BackendServiceStopReason.ServiceControlManager)
            {
                _stopReason = reason;
                _exitCode = 0;
            }
        }
    }

    public void MarkServiceControlManagerStop(BackendServiceStopReason markerReason)
    {
        lock (_syncRoot)
        {
            if (_stopReason != BackendServiceStopReason.Unknown)
            {
                return;
            }

            _stopReason = IsIntentionalReason(markerReason)
                ? markerReason
                : BackendServiceStopReason.ServiceControlManager;
            _exitCode = 0;
        }
    }

    public void MarkStartupFailure()
    {
        lock (_syncRoot)
        {
            _stopReason = BackendServiceStopReason.StartupFailure;
            _exitCode = StartupFailureExitCode;
        }
    }

    public void MarkUnexpectedRunReturn()
    {
        lock (_syncRoot)
        {
            if (_stopReason == BackendServiceStopReason.Unknown)
            {
                _stopReason = BackendServiceStopReason.UnexpectedProcessExit;
                _exitCode = StartupFailureExitCode;
            }
        }
    }

    private static bool IsIntentionalReason(BackendServiceStopReason reason)
        => reason is BackendServiceStopReason.FrontendRequest
            or BackendServiceStopReason.WindowsShutdown
            or BackendServiceStopReason.ForceRepair
            or BackendServiceStopReason.Uninstall
            or BackendServiceStopReason.ExplicitServiceStop
            or BackendServiceStopReason.InstallRepair;
}

internal sealed record ServiceCommandResult(bool Success, int ExitCode, string Detail);

internal sealed record ServiceConfigurationSnapshot(
    bool Exists,
    string ServiceName,
    string? ImagePath,
    string? ServiceExePath,
    int? StartValue,
    string ConfiguredStartType,
    bool DelayedAutoStart,
    bool FailureActionsPresent,
    bool FailureActionsOnNonCrashFailures,
    string CurrentStatus,
    string SystemDrive,
    string ServiceExeDrive)
{
    public bool MatchesRequestedStartupMode(string requestedMode) => requestedMode switch
    {
        "delayed-auto" => StartValue == 2 && DelayedAutoStart,
        "auto" => StartValue == 2 && !DelayedAutoStart,
        "demand" => StartValue == 3,
        _ => false
    };

    public bool FailureRecoveryConfigured => FailureActionsPresent && FailureActionsOnNonCrashFailures;
}

internal sealed record ServiceRuntimeConvergenceResult(bool Success, string Code, string Detail);

internal sealed record ServiceAutostartTransitionResult(
    bool Success,
    string Code,
    string Detail,
    bool PolicyCommitted,
    int ScExitCode,
    string ActualStartupMode,
    string ServiceStatus);

internal static class ServiceAutostartTransition
{
    public static ServiceAutostartTransitionResult Execute(
        bool enabled,
        bool serviceExists,
        string requestedStartupMode,
        Func<ServiceCommandResult> configureStartupMode,
        Func<ServiceConfigurationSnapshot> readConfiguration,
        Func<ServiceCommandResult> configureRecovery,
        Func<ServiceRuntimeConvergenceResult> convergeRuntime,
        Action commitPolicy)
    {
        if (!serviceExists)
        {
            if (!enabled)
            {
                try
                {
                    commitPolicy();
                    return new ServiceAutostartTransitionResult(
                        true,
                        "NOT_INSTALLED",
                        "Service was not installed; the disabled user policy was committed.",
                        true,
                        -1,
                        "Missing",
                        "Missing");
                }
                catch (Exception ex)
                {
                    return new ServiceAutostartTransitionResult(
                        false,
                        "AUTOSTART_POLICY_WRITE_FAILED",
                        ex.Message,
                        false,
                        -1,
                        "Missing",
                        "Missing");
                }
            }

            return new ServiceAutostartTransitionResult(
                false,
                "SERVICE_NOT_INSTALLED",
                "The backend service is not installed.",
                false,
                -1,
                "Missing",
                "Missing");
        }

        var scResult = configureStartupMode();
        if (!scResult.Success)
        {
            return new ServiceAutostartTransitionResult(
                false,
                "SERVICE_STARTUP_CONFIG_FAILED",
                scResult.Detail,
                false,
                scResult.ExitCode,
                "Unverified",
                readConfiguration().CurrentStatus);
        }

        var configuration = readConfiguration();
        if (!configuration.MatchesRequestedStartupMode(requestedStartupMode))
        {
            return new ServiceAutostartTransitionResult(
                false,
                "SERVICE_STARTUP_CONFIG_MISMATCH",
                $"RequestedStartupMode={requestedStartupMode}, ActualStartupMode={configuration.ConfiguredStartType}.",
                false,
                scResult.ExitCode,
                configuration.ConfiguredStartType,
                configuration.CurrentStatus);
        }

        if (enabled)
        {
            var recoveryResult = configureRecovery();
            if (!recoveryResult.Success)
            {
                return new ServiceAutostartTransitionResult(
                    false,
                    "SERVICE_RECOVERY_CONFIG_FAILED",
                    recoveryResult.Detail,
                    false,
                    recoveryResult.ExitCode,
                    configuration.ConfiguredStartType,
                    configuration.CurrentStatus);
            }

            configuration = readConfiguration();
            if (!configuration.FailureRecoveryConfigured)
            {
                return new ServiceAutostartTransitionResult(
                    false,
                    "SERVICE_RECOVERY_CONFIG_MISMATCH",
                    "SCM recovery settings could not be verified.",
                    false,
                    recoveryResult.ExitCode,
                    configuration.ConfiguredStartType,
                    configuration.CurrentStatus);
            }

            var runtimeResult = convergeRuntime();
            if (!runtimeResult.Success)
            {
                configuration = readConfiguration();
                return new ServiceAutostartTransitionResult(
                    false,
                    runtimeResult.Code,
                    runtimeResult.Detail,
                    false,
                    scResult.ExitCode,
                    configuration.ConfiguredStartType,
                    configuration.CurrentStatus);
            }
        }

        try
        {
            commitPolicy();
        }
        catch (Exception ex)
        {
            configuration = readConfiguration();
            return new ServiceAutostartTransitionResult(
                false,
                "AUTOSTART_POLICY_WRITE_FAILED",
                ex.Message,
                false,
                scResult.ExitCode,
                configuration.ConfiguredStartType,
                configuration.CurrentStatus);
        }

        configuration = readConfiguration();
        return new ServiceAutostartTransitionResult(
            true,
            enabled ? "STARTUP_AUTO" : "STARTUP_MANUAL",
            enabled ? configuration.ConfiguredStartType : "Manual",
            true,
            scResult.ExitCode,
            configuration.ConfiguredStartType,
            configuration.CurrentStatus);
    }
}

internal static class ServiceStopReasonMarker
{
    private static readonly TimeSpan MarkerLifetime = TimeSpan.FromMinutes(5);

    public static void Write(string path, BackendServiceStopReason reason)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        File.WriteAllText(path, $"{reason}|{DateTimeOffset.UtcNow:O}");
    }

    public static BackendServiceStopReason Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return BackendServiceStopReason.Unknown;
            }

            var parts = File.ReadAllText(path).Trim().Split('|', 2);
            if (parts.Length != 2
                || !Enum.TryParse<BackendServiceStopReason>(parts[0], ignoreCase: true, out var reason)
                || !DateTimeOffset.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var createdAtUtc))
            {
                return BackendServiceStopReason.Unknown;
            }

            var age = DateTimeOffset.UtcNow - createdAtUtc;
            return age >= TimeSpan.Zero && age <= MarkerLifetime
                ? reason
                : BackendServiceStopReason.Unknown;
        }
        catch
        {
            return BackendServiceStopReason.Unknown;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
