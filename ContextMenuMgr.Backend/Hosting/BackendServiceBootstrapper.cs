using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Hosting;

/// <summary>
/// Performs elevated service install/repair operations for the frontend.
/// </summary>
internal static class BackendServiceBootstrapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceMarkedForDelete = 1072;
    private const int ErrorServiceExists = 1073;
    private const int ErrorAccessDenied = 5;
    private const int ScManagerConnect = 0x0001;
    private const int ServiceQueryStatus = 0x0004;
    private const int ServiceDelete = 0x00010000;
    private const string FrontendPolicyKeyPath = @"Software\ContextMenuMgr\Frontend";
    private const string FrontendPolicyValueName = "StartWithWindows";
    private static readonly string DataDirectory = RuntimePaths.DataDirectory;
    private static readonly string KeepFrontendOnStopMarkerPath = Path.Combine(
        DataDirectory,
        ServiceMetadata.KeepFrontendOnStopMarkerFileName);
    private static readonly string StopReasonMarkerPath = Path.Combine(
        DataDirectory,
        ServiceMetadata.StopReasonMarkerFileName);
    private static readonly string BootstrapLogPath = Path.Combine(RuntimePaths.LogsDirectory, "bootstrap.log");
    private const int RecoveryResetPeriodSeconds = 24 * 60 * 60;
    private const string RecoveryActions = "restart/5000/restart/15000/restart/60000";

    /// <summary>
    /// Tries to execute an elevated backend bootstrap command.
    /// </summary>
    public static bool TryRun(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "--service-bootstrap", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var command = args.Length >= 2 ? args[1] : string.Empty;
        var resultFilePath = TryGetArgumentValue(args, "--result-file");
        if (string.IsNullOrWhiteSpace(resultFilePath))
        {
            Environment.ExitCode = 1;
            return true;
        }

        AppendBootstrapLog($"BootstrapStart: Command={command}, ResultFilePresent={!string.IsNullOrWhiteSpace(resultFilePath)}, UserSidPresent={!string.IsNullOrWhiteSpace(TryGetArgumentValue(args, "--user-sid"))}, EnabledArgument={TryGetArgumentValue(args, "--enabled")}, ArgsCount={args.Length}.");
        var result = Execute(command, args);
        AppendBootstrapLog($"BootstrapEnd: Command={command}, Success={result.Success}, Code={result.Code}, Detail={result.Detail}.");
        WriteResult(resultFilePath, result.Success, result.Code, result.Detail);
        Environment.ExitCode = result.Success ? 0 : 1;
        return true;
    }

    private static (bool Success, string Code, string Detail) Execute(string command, IReadOnlyList<string> args)
    {
        var details = new List<string>
        {
            $"Command={command}",
            $"ResultFilePresent={!string.IsNullOrWhiteSpace(TryGetArgumentValue(args, "--result-file"))}",
            $"UserSidPresent={!string.IsNullOrWhiteSpace(TryGetArgumentValue(args, "--user-sid"))}",
            $"EnabledArgument={TryGetArgumentValue(args, "--enabled") ?? "<null>"}",
            $"Identity={WindowsIdentity.GetCurrent().Name}",
            $"IsSystem={WindowsIdentity.GetCurrent().User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true}",
            $"IsAdmin={IsCurrentProcessAdmin()}"
        };
        void AddDetail(string detail)
        {
            details.Add(detail);
            AppendBootstrapLog(detail);
        }

        try
        {
            var userSidArgument = TryGetUserSidArgument(args);
            AddDetail($"UserSidArgument: Present={!string.IsNullOrWhiteSpace(userSidArgument.Sid)}, Valid={userSidArgument.IsValid}, Sid={userSidArgument.Sid ?? "<null>"}, Detail={userSidArgument.Detail ?? "<null>"}.");
            if (!userSidArgument.IsValid)
            {
                return (false, "INVALID_USER_SID", JoinDetails(details, userSidArgument.Detail ?? "Invalid --user-sid."));
            }

            var result = command.ToLowerInvariant() switch
            {
                "install-or-repair" => InstallOrRepairService(userSidArgument.Sid, AddDetail),
                "uninstall" => UninstallService(AddDetail),
                "force-remove-service" => ForceRemoveService(AddDetail),
                "stop" => StopService(AddDetail),
                "set-startup-mode" => SetServiceStartupMode(TryParseEnabledArgument(args), userSidArgument.Sid, AddDetail),
                "repair-runtime-data-acl" => RepairRuntimeDataAcl(AddDetail),
                _ => (false, "UNKNOWN_BOOTSTRAP_COMMAND", command)
            };
            return (result.Item1, result.Item2, JoinDetails(details, result.Item3));
        }
        catch (Exception ex)
        {
            var status = GetServiceStatusText(ServiceMetadata.ServiceName);
            AddDetail($"BootstrapException: Exception={ex}, Status={status}.");
            return (false, "SERVICE_BOOTSTRAP_ERROR", JoinDetails(details, $"{ex.Message} | Status={status}"));
        }
    }

    private static (bool Success, string Code, string Detail) InstallOrRepairService(
        string? userSid,
        Action<string> log,
        bool? requestedAutostartEnabled = null,
        bool persistRequestedPolicyOnSuccess = false)
    {
        var serviceExePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(serviceExePath) || !File.Exists(serviceExePath))
        {
            return (false, "BACKEND_EXE_MISSING", string.Empty);
        }

        var binaryPath = $"\"{serviceExePath}\" --service";
        var isAutostartEnabled = requestedAutostartEnabled ?? IsAutostartEnabledForUser(userSid, log);
        var startupMode = GetRequestedStartupMode(isAutostartEnabled, serviceExePath);
        var createStartupMode = isAutostartEnabled ? "auto" : "demand";
        LogServiceLifecycleDiagnostics(log, userSid, isAutostartEnabled, startupMode);
        log($"InstallOrRepairService: ServiceExePath={serviceExePath}, BinaryPath={binaryPath}, StartupMode={startupMode}, UserSid={userSid ?? "<null>"}, IsAutostartEnabledForUser={isAutostartEnabled}, ServiceExistsScm={ServiceExistsInScm(ServiceMetadata.ServiceName)}, LegacyServiceExistsScm={ServiceExistsInScm(ServiceMetadata.LegacyServiceName)}.");

        var health = TestServiceRegistrationHealthy(ServiceMetadata.ServiceName);
        log($"InstallOrRepairServiceHealthCheck: ServiceName={ServiceMetadata.ServiceName}, Healthy={health.Healthy}, Reason={health.Reason}.");
        if (ServiceExistsInScm(ServiceMetadata.ServiceName) && !health.Healthy)
        {
            log($"InstallOrRepairService: Existing service unhealthy, Reason={health.Reason}. Removing before create.");
            var removal = RemoveServiceRegistrationTolerant(
                ServiceMetadata.ServiceName,
                keepFrontendAlive: true,
                log,
                health.Reason);
            if (!removal.Success)
            {
                return (false, removal.Code, removal.Detail);
            }
        }

        if (ServiceExistsInScm(ServiceMetadata.LegacyServiceName))
        {
            var legacyRemoval = RemoveServiceRegistrationTolerant(
                ServiceMetadata.LegacyServiceName,
                keepFrontendAlive: true,
                log,
                "LEGACY_SERVICE_CLEANUP");
            if (!legacyRemoval.Success)
            {
                return (false, legacyRemoval.Code, legacyRemoval.Detail);
            }
        }

        if (!ServiceExistsInScm(ServiceMetadata.ServiceName))
        {
            var createResult = TryRunSc(log,
                "create",
                ServiceMetadata.ServiceName,
                "binPath=",
                binaryPath,
                "start=",
                createStartupMode,
                "DisplayName=",
                ServiceMetadata.DisplayName);
            if (!createResult.Success)
            {
                if (createResult.ExitCode == ErrorServiceExists)
                {
                    var recovery = RecoverCreateServiceAlreadyExists(log);
                    if (!recovery.Success)
                    {
                        return (false, recovery.Code, recovery.Detail);
                    }

                    if (!ServiceExistsInScm(ServiceMetadata.ServiceName))
                    {
                        createResult = TryRunSc(log,
                            "create",
                            ServiceMetadata.ServiceName,
                            "binPath=",
                            binaryPath,
                            "start=",
                            createStartupMode,
                            "DisplayName=",
                            ServiceMetadata.DisplayName);
                    }
                    else
                    {
                        var configExistingResult = TryRunSc(log,
                            "config",
                            ServiceMetadata.ServiceName,
                            "binPath=",
                            binaryPath);
                        if (!configExistingResult.Success)
                        {
                            return (false, "SERVICE_CONFIG_FAILED", configExistingResult.Detail);
                        }

                        createResult = new ScResult(true, 0, string.Empty, string.Empty, "Existing healthy service will be configured.");
                    }
                }
                else if (createResult.ExitCode == ErrorServiceMarkedForDelete)
                {
                    return (
                        false,
                        "SERVICE_PENDING_DELETE",
                        "Service is marked for deletion. Close Services MMC, Task Manager service tab, or any process holding the service handle, then retry; reboot if it remains pending.");
                }

                if (!createResult.Success)
                {
                    return (false, "SERVICE_CREATE_FAILED", createResult.Detail);
                }
            }
        }
        else
        {
            var configResult = TryRunSc(log,
                "config",
                ServiceMetadata.ServiceName,
                "binPath=",
                binaryPath);
            if (!configResult.Success)
            {
                return (false, "SERVICE_CONFIG_FAILED", configResult.Detail);
            }
        }

        health = TestServiceRegistrationHealthy(ServiceMetadata.ServiceName);
        if (!health.Healthy)
        {
            return (false, "SERVICE_REGISTRATION_INCOMPLETE", $"Service registration health check failed. Reason={health.Reason}.");
        }

        var registeredConfiguration = ReadServiceConfiguration(ServiceMetadata.ServiceName);
        if (!PathsReferToSameFile(registeredConfiguration.ServiceExePath, serviceExePath))
        {
            return (
                false,
                "SERVICE_IMAGE_PATH_MISMATCH",
                $"Configured service executable does not match this backend. Expected={serviceExePath}, Actual={registeredConfiguration.ServiceExePath ?? "<missing>"}.");
        }

        var descriptionResult = TryRunSc(log, "description", ServiceMetadata.ServiceName, "Context Menu Manager Plus elevated backend service");
        if (!descriptionResult.Success)
        {
            return (false, "SERVICE_DESCRIPTION_CONFIG_FAILED", descriptionResult.Detail);
        }

        if (persistRequestedPolicyOnSuccess)
        {
            var transition = ServiceAutostartTransition.Execute(
                enabled: isAutostartEnabled,
                serviceExists: true,
                requestedStartupMode: startupMode,
                configureStartupMode: () =>
                {
                    var result = TryRunSc(log, "config", ServiceMetadata.ServiceName, "start=", startupMode);
                    return new ServiceCommandResult(result.Success, result.ExitCode, result.Detail);
                },
                readConfiguration: () => ReadServiceConfiguration(ServiceMetadata.ServiceName),
                configureRecovery: () =>
                {
                    var result = ConfigureAndVerifyServiceRecovery(log);
                    return new ServiceCommandResult(result.Success, result.Success ? 0 : -1, result.Detail);
                },
                convergeRuntime: () => ConvergeServiceRuntime(serviceExePath, log),
                commitPolicy: () => SetAutostartPolicyForUser(userSid, isAutostartEnabled, log));

            log($"ServiceStartupModeResult: RequestedStartupMode={startupMode}, ActualStartupMode={transition.ActualStartupMode}, ScExitCode={transition.ScExitCode}, ServiceStatus={transition.ServiceStatus}, PolicyCommitted={transition.PolicyCommitted}, Success={transition.Success}, Code={transition.Code}.");
            if (!transition.Success)
            {
                TryDeleteKeepFrontendMarker();
                return (false, transition.Code, transition.Detail);
            }

            if (isAutostartEnabled)
            {
                TryEnsureTrayHostViaPipe(log);
            }
        }
        else
        {
            var startupConfigurationResult = ConfigureAndVerifyStartupMode(startupMode, log);
            if (!startupConfigurationResult.Success)
            {
                return (false, startupConfigurationResult.Code, startupConfigurationResult.Detail);
            }

            var recoveryResult = ConfigureAndVerifyServiceRecovery(log);
            if (!recoveryResult.Success)
            {
                return (false, recoveryResult.Code, recoveryResult.Detail);
            }

            var convergence = ConvergeServiceRuntime(serviceExePath, log);
            if (!convergence.Success)
            {
                TryDeleteKeepFrontendMarker();
                return (false, convergence.Code, convergence.Detail);
            }
        }

        TryDeleteKeepFrontendMarker();
        LogServiceLifecycleDiagnostics(log, userSid, isAutostartEnabled, startupMode);
        return (true, "OK", "Running");
    }

    private static (bool Success, string Code, string Detail) UninstallService(Action<string> log)
    {
        var primary = RemoveServiceRegistrationTolerant(
            ServiceMetadata.ServiceName,
            keepFrontendAlive: true,
            log,
            "UNINSTALL_PRIMARY");
        var legacy = RemoveServiceRegistrationTolerant(
            ServiceMetadata.LegacyServiceName,
            keepFrontendAlive: true,
            log,
            "UNINSTALL_LEGACY");
        TryDeleteKeepFrontendMarker();

        if (primary.IsPendingDelete || legacy.IsPendingDelete)
        {
            var detail = JoinDetails(
                new[] { primary.Detail, legacy.Detail },
                "Close Services MMC, Task Manager service tab, or any process holding the service handle, then retry; reboot if it remains pending.");
            log($"UninstallServiceFinal: Code=SERVICE_PENDING_DELETE, Detail={detail}.");
            return (false, "SERVICE_PENDING_DELETE", detail);
        }

        if (!primary.Success)
        {
            log($"UninstallServiceFinal: Code={primary.Code}, Detail={primary.Detail}.");
            return (false, primary.Code, primary.Detail);
        }

        if (!legacy.Success)
        {
            log($"UninstallServiceFinal: Code={legacy.Code}, Detail={legacy.Detail}.");
            return (false, legacy.Code, legacy.Detail);
        }

        var code = primary.Code == "NOT_INSTALLED" && legacy.Code == "NOT_INSTALLED"
            ? "NOT_INSTALLED"
            : "UNINSTALLED";
        log($"UninstallServiceFinal: Code={code}, Primary={primary.Code}, Legacy={legacy.Code}.");
        return (true, code, code == "NOT_INSTALLED" ? "Service was not installed." : "Service removed.");
    }

    private static (bool Success, string Code, string Detail) ForceRemoveService(Action<string> log)
    {
        var primary = RemoveServiceRegistrationTolerant(
            ServiceMetadata.ServiceName,
            keepFrontendAlive: true,
            log,
            "FORCE_REMOVE_PRIMARY");
        var legacy = RemoveServiceRegistrationTolerant(
            ServiceMetadata.LegacyServiceName,
            keepFrontendAlive: true,
            log,
            "FORCE_REMOVE_LEGACY");
        TryDeleteKeepFrontendMarker();

        if (primary.IsPendingDelete || legacy.IsPendingDelete)
        {
            var detail = JoinDetails(
                new[] { primary.Detail, legacy.Detail },
                "Close Services MMC, Task Manager service tab, or any process holding the service handle, then retry; reboot if it remains pending.");
            log($"ForceRemoveServiceFinal: Code=SERVICE_PENDING_DELETE, Detail={detail}.");
            return (false, "SERVICE_PENDING_DELETE", detail);
        }

        if (!primary.Success)
        {
            log($"ForceRemoveServiceFinal: Code={primary.Code}, Detail={primary.Detail}.");
            return (false, primary.Code, primary.Detail);
        }

        if (!legacy.Success)
        {
            log($"ForceRemoveServiceFinal: Code={legacy.Code}, Detail={legacy.Detail}.");
            return (false, legacy.Code, legacy.Detail);
        }

        log($"ForceRemoveServiceFinal: Code=FORCE_REMOVED, Primary={primary.Code}, Legacy={legacy.Code}.");
        return (true, "FORCE_REMOVED", "Service registrations removed.");
    }

    private static (bool Success, string Code, string Detail) StopService(Action<string> log)
    {
        if (!ServiceExistsInScm(ServiceMetadata.ServiceName))
        {
            return (true, "NOT_INSTALLED", "Service was not installed.");
        }

        using var service = new ServiceController(ServiceMetadata.ServiceName);
        if (service.Status == ServiceControllerStatus.Stopped)
        {
            return (true, "ALREADY_STOPPED", "Stopped");
        }

        TryWriteStopReasonMarker(BackendServiceStopReason.ExplicitServiceStop, log);
        try
        {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            var status = GetServiceStatusText(ServiceMetadata.ServiceName);
            log($"StopService: ServiceName={ServiceMetadata.ServiceName}, Status={status}.");
            return string.Equals(status, nameof(ServiceControllerStatus.Stopped), StringComparison.OrdinalIgnoreCase)
                ? (true, "STOPPED", "Stopped")
                : (false, "SERVICE_NOT_STOPPED", status);
        }
        finally
        {
            ServiceStopReasonMarker.Delete(StopReasonMarkerPath);
        }
    }

    private static (bool Success, string Code, string Detail) SetServiceStartupMode(bool enabled, string? userSid, Action<string> log)
    {
        log($"SetServiceStartupMode: Enabled={enabled}, UserSid={userSid ?? "<null>"}.");

        if (enabled)
        {
            // Enabling autostart is a convergence operation, not just a Start
            // value mutation. Reuse the install/repair health, SCM Running, and
            // real pipe-Ping path so the policy is committed only at the end.
            var enabledResult = InstallOrRepairService(
                userSid,
                log,
                requestedAutostartEnabled: true,
                persistRequestedPolicyOnSuccess: true);
            return enabledResult.Success
                ? (true, "STARTUP_AUTO", enabledResult.Detail)
                : enabledResult;
        }

        var serviceExists = ServiceExistsInScm(ServiceMetadata.ServiceName);
        var transition = ServiceAutostartTransition.Execute(
            enabled: false,
            serviceExists,
            requestedStartupMode: "demand",
            configureStartupMode: () =>
            {
                var result = TryRunSc(log, "config", ServiceMetadata.ServiceName, "start=", "demand");
                return new ServiceCommandResult(result.Success, result.ExitCode, result.Detail);
            },
            readConfiguration: () => ReadServiceConfiguration(ServiceMetadata.ServiceName),
            configureRecovery: static () => new ServiceCommandResult(true, 0, "Not required while disabling autostart."),
            convergeRuntime: static () => new ServiceRuntimeConvergenceResult(true, "OK", "Not required while disabling autostart."),
            commitPolicy: () => SetAutostartPolicyForUser(userSid, enabled: false, log));

        log($"ServiceStartupModeResult: RequestedStartupMode=demand, ActualStartupMode={transition.ActualStartupMode}, ScExitCode={transition.ScExitCode}, ServiceStatus={transition.ServiceStatus}, PolicyCommitted={transition.PolicyCommitted}, Success={transition.Success}, Code={transition.Code}.");
        return (transition.Success, transition.Code, transition.Detail);
    }

    private static (bool Success, string Code, string Detail) RepairRuntimeDataAcl(Action<string> log)
    {
        log($"RepairRuntimeDataAcl: Root={RuntimePaths.RootDirectory}, Result=Started.");
        var result = RuntimeDataAclRepairService.RepairRuntimeDataDirectory(RuntimePaths.RootDirectory);
        log($"RepairRuntimeDataAcl: Root={RuntimePaths.RootDirectory}, Success={result.Success}, Code={result.Code}, Detail={result.Detail}");
        return (result.Success, result.Code, result.Detail);
    }

    private static ServiceRemovalResult RemoveServiceRegistrationTolerant(
        string serviceName,
        bool keepFrontendAlive,
        Action<string> log,
        string reason)
    {
        if (!IsManagedServiceName(serviceName))
        {
            return new ServiceRemovalResult(false, "UNSUPPORTED_SERVICE_NAME", $"Refusing to remove unsupported service name '{serviceName}'.", false);
        }

        log($"RemoveServiceRegistrationTolerantStart: ServiceName={serviceName}, Reason={reason}, KeepFrontendAlive={keepFrontendAlive}.");
        var existsInScm = ServiceExistsInScm(serviceName);
        var existsInRegistry = ServiceExistsInRegistry(serviceName);
        log($"ServiceExistsScm: ServiceName={serviceName}, Exists={existsInScm}.");
        log($"ServiceExistsRegistry: ServiceName={serviceName}, Exists={existsInRegistry}.");

        var statusText = GetServiceStatusText(serviceName);
        log($"ServiceStatusBeforeStop: ServiceName={serviceName}, Status={statusText}.");
        if (existsInScm && !string.Equals(statusText, nameof(ServiceControllerStatus.Stopped), StringComparison.OrdinalIgnoreCase))
        {
            TryWriteStopReasonMarker(MapRemovalStopReason(reason), log);
            var disableFailureFlag = TryRunSc(log, "failureflag", serviceName, "0");
            log($"RemovalRecoverySuppression: ServiceName={serviceName}, Success={disableFailureFlag.Success}, ScExitCode={disableFailureFlag.ExitCode}. Normal SCM stop remains non-recoverable even if this best-effort guard fails.");
            if (keepFrontendAlive)
            {
                TryEnsureKeepFrontendMarker(log);
            }

            try
            {
                using var service = new ServiceController(serviceName);
                service.Stop();
                log($"StopAttemptResult: ServiceName={serviceName}, Result=StopCalled.");
            }
            catch (Exception ex)
            {
                log($"StopAttemptResult: ServiceName={serviceName}, Result=Failure, StopFailureIgnoredForDelete=true, Exception={ex}.");
            }

            try
            {
                using var service = new ServiceController(serviceName);
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                log($"StopAttemptResult: ServiceName={serviceName}, Result=WaitStopped, FinalStatus={TryGetServiceControllerStatusText(service)}.");
            }
            catch (Exception ex)
            {
                log($"StopAttemptResult: ServiceName={serviceName}, Result=WaitFailure, StopFailureIgnoredForDelete=true, Exception={ex}.");
            }
        }

        var deleteResult = TryDeleteServiceRegistration(serviceName);
        log($"DeleteAttemptResult: ServiceName={serviceName}, Success={deleteResult.Success}, PendingDelete={deleteResult.PendingDelete}, NotInstalled={deleteResult.NotInstalled}, Fatal={deleteResult.Fatal}, ErrorCode={deleteResult.ErrorCode}, Detail={deleteResult.Detail}.");
        if (deleteResult.Fatal)
        {
            ServiceStopReasonMarker.Delete(StopReasonMarkerPath);
            return new ServiceRemovalResult(false, deleteResult.Code, deleteResult.Detail, deleteResult.PendingDelete);
        }

        var wait = WaitForScmRemoval(serviceName, TimeSpan.FromSeconds(10), log);
        ServiceStopReasonMarker.Delete(StopReasonMarkerPath);
        log($"WaitForScmRemoval result: ServiceName={serviceName}, Code={wait.Code}, Detail={wait.Detail}.");
        log($"RemoveServiceRegistrationTolerantFinal: ServiceName={serviceName}, Code={wait.Code}, Success={wait.Success}, PendingDelete={wait.IsPendingDelete}.");
        return wait;
    }

    private static ServiceRemovalResult RecoverCreateServiceAlreadyExists(Action<string> log)
    {
        log("CreateServiceAlreadyExistsRecovery: Result=Start.");
        var health = TestServiceRegistrationHealthy(ServiceMetadata.ServiceName);
        log($"CreateServiceAlreadyExistsRecovery: ServiceExistsScm={ServiceExistsInScm(ServiceMetadata.ServiceName)}, Healthy={health.Healthy}, Reason={health.Reason}.");
        if (health.Healthy)
        {
            return new ServiceRemovalResult(true, "SERVICE_EXISTS_HEALTHY", "Service already exists and appears healthy; continuing with config.", false);
        }

        return RemoveServiceRegistrationTolerant(
            ServiceMetadata.ServiceName,
            keepFrontendAlive: true,
            log,
            $"CREATE_1073_RECOVERY_{health.Reason}");
    }

    private static bool ServiceExistsInScm(string serviceName)
    {
        var query = TryOpenService(serviceName, ServiceQueryStatus);
        query.Handle?.Dispose();
        return query.Success;
    }

    private static bool ServiceExistsInRegistry(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static ServiceHealthResult TestServiceRegistrationHealthy(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
        if (key is null)
        {
            return new ServiceHealthResult(false, "SERVICE_REGISTRY_KEY_MISSING");
        }

        var imagePath = key.GetValue("ImagePath") as string;
        var start = key.GetValue("Start");
        var type = key.GetValue("Type");
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return new ServiceHealthResult(false, "SERVICE_IMAGE_PATH_MISSING");
        }

        if (start is null)
        {
            return new ServiceHealthResult(false, "SERVICE_START_VALUE_MISSING");
        }

        if (type is null)
        {
            return new ServiceHealthResult(false, "SERVICE_TYPE_VALUE_MISSING");
        }

        var executablePath = TryParseExecutablePath(imagePath);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return new ServiceHealthResult(false, $"SERVICE_IMAGE_EXECUTABLE_MISSING Path={executablePath ?? "<unparsed>"} ImagePath={imagePath}");
        }

        return new ServiceHealthResult(true, "OK");
    }

    internal static string GetRequestedStartupMode(
        bool enabled,
        string serviceExePath,
        string? windowsDirectory = null)
    {
        if (!enabled)
        {
            return "demand";
        }

        return ShouldUseDelayedAutoStart(serviceExePath, windowsDirectory)
            ? "delayed-auto"
            : "auto";
    }

    internal static bool ShouldUseDelayedAutoStart(string serviceExePath, string? windowsDirectory = null)
    {
        var systemDrive = GetPathRoot(windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var serviceDrive = GetPathRoot(serviceExePath);
        return !string.IsNullOrWhiteSpace(systemDrive)
               && !string.IsNullOrWhiteSpace(serviceDrive)
               && !string.Equals(systemDrive, serviceDrive, StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string[]> BuildRecoveryScCommands(string serviceName)
        =>
        [
            [
                "failure",
                serviceName,
                "reset=",
                RecoveryResetPeriodSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "actions=",
                RecoveryActions
            ],
            ["failureflag", serviceName, "1"]
        ];

    private static (bool Success, string Code, string Detail) ConfigureAndVerifyStartupMode(
        string requestedStartupMode,
        Action<string> log)
    {
        var scResult = TryRunSc(
            log,
            "config",
            ServiceMetadata.ServiceName,
            "start=",
            requestedStartupMode);
        var configuration = ReadServiceConfiguration(ServiceMetadata.ServiceName);
        log($"ServiceStartupModeResult: RequestedStartupMode={requestedStartupMode}, ActualStartupMode={configuration.ConfiguredStartType}, DelayedAutoStart={configuration.DelayedAutoStart}, ScExitCode={scResult.ExitCode}, ServiceStatus={configuration.CurrentStatus}.");

        if (!scResult.Success)
        {
            return (false, "SERVICE_STARTUP_CONFIG_FAILED", scResult.Detail);
        }

        if (!configuration.MatchesRequestedStartupMode(requestedStartupMode))
        {
            return (
                false,
                "SERVICE_STARTUP_CONFIG_MISMATCH",
                $"RequestedStartupMode={requestedStartupMode}, ActualStartupMode={configuration.ConfiguredStartType}, DelayedAutoStart={configuration.DelayedAutoStart}.");
        }

        return (true, "OK", configuration.ConfiguredStartType);
    }

    private static (bool Success, string Code, string Detail) ConfigureAndVerifyServiceRecovery(Action<string> log)
    {
        foreach (var arguments in BuildRecoveryScCommands(ServiceMetadata.ServiceName))
        {
            var result = TryRunSc(log, arguments);
            if (!result.Success)
            {
                log($"ServiceRecoveryConfiguration: FailureRecoveryConfigured=False, FailedCommand={arguments[0]}, ScExitCode={result.ExitCode}, Detail={result.Detail}.");
                return (false, "SERVICE_RECOVERY_CONFIG_FAILED", result.Detail);
            }
        }

        var queryFailure = TryRunSc(log, "qfailure", ServiceMetadata.ServiceName);
        var queryFailureFlag = TryRunSc(log, "qfailureflag", ServiceMetadata.ServiceName);
        if (!queryFailure.Success || !queryFailureFlag.Success)
        {
            var failed = !queryFailure.Success ? queryFailure : queryFailureFlag;
            return (false, "SERVICE_RECOVERY_VERIFY_FAILED", failed.Detail);
        }

        var configuration = ReadServiceConfiguration(ServiceMetadata.ServiceName);
        log($"ServiceRecoveryConfiguration: FailureRecoveryConfigured={configuration.FailureRecoveryConfigured}, FailureActionsPresent={configuration.FailureActionsPresent}, FailureActionsOnNonCrashFailures={configuration.FailureActionsOnNonCrashFailures}, ResetSeconds={RecoveryResetPeriodSeconds}, Actions={RecoveryActions}.");
        return configuration.FailureRecoveryConfigured
            ? (true, "OK", RecoveryActions)
            : (false, "SERVICE_RECOVERY_CONFIG_MISMATCH", "SCM recovery settings could not be verified after configuration.");
    }

    private static ServiceRuntimeConvergenceResult ConvergeServiceRuntime(
        string serviceExePath,
        Action<string> log)
    {
        using (var service = new ServiceController(ServiceMetadata.ServiceName))
        {
            var initialStatus = service.Status;
            log($"ServiceStartCheck: ServiceName={ServiceMetadata.ServiceName}, InitialStatus={initialStatus}.");
            if (initialStatus != ServiceControllerStatus.Running)
            {
                log("ServiceStart: Ensuring keep-frontend marker before service start.");
                EnsureKeepFrontendMarker();
                try
                {
                    log("ServiceStart: Calling Start.");
                    service.Start();
                    log("ServiceStart: WaitForStatus Running started, Timeout=15s.");
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    service.Refresh();
                    log($"ServiceStart: WaitForStatus succeeded, Status={service.Status}.");
                }
                catch (System.ServiceProcess.TimeoutException ex)
                {
                    var finalStatus = TryGetServiceControllerStatusText(service);
                    log($"ServiceStart: WaitForStatus timeout/failure, FinalStatus={finalStatus}, Exception={ex}.");
                    TryDeleteKeepFrontendMarker();
                    return new ServiceRuntimeConvergenceResult(
                        false,
                        "SERVICE_START_TIMEOUT",
                        $"Service did not report Running within 15 seconds. InitialStatus={initialStatus}, FinalStatus={finalStatus}, ServiceName={ServiceMetadata.ServiceName}, ServiceExePath={serviceExePath}, Exception={ex.Message}. Check {RuntimePaths.LogsDirectory}\\bootstrap.log, backend.log, and service-startup.log.");
                }
                catch (Exception ex)
                {
                    var finalStatus = TryGetServiceControllerStatusText(service);
                    log($"ServiceStart: Start/wait failed, FinalStatus={finalStatus}, Exception={ex}.");
                    TryDeleteKeepFrontendMarker();
                    return new ServiceRuntimeConvergenceResult(
                        false,
                        "SERVICE_START_FAILED",
                        $"Service could not be started. InitialStatus={initialStatus}, FinalStatus={finalStatus}, ServiceName={ServiceMetadata.ServiceName}, ServiceExePath={serviceExePath}, Exception={ex.Message}.");
                }
            }
            else
            {
                log($"ServiceStart: Already running, Status={initialStatus}.");
            }
        }

        var status = GetServiceStatusText(ServiceMetadata.ServiceName);
        if (!string.Equals(status, nameof(ServiceControllerStatus.Running), StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteKeepFrontendMarker();
            return new ServiceRuntimeConvergenceResult(false, "SERVICE_NOT_RUNNING", status);
        }

        // SCM Running is not sufficient: the runtime is healthy only after a
        // real request/response round trip through the backend pipe.
        if (!WaitForBackendPipeReady(TimeSpan.FromSeconds(20), log))
        {
            var finalStatus = GetServiceStatusText(ServiceMetadata.ServiceName);
            log($"BackendPipeReadyFailure: ServiceName={ServiceMetadata.ServiceName}, FinalStatus={finalStatus}.");
            TryDeleteKeepFrontendMarker();
            return new ServiceRuntimeConvergenceResult(
                false,
                "BACKEND_PIPE_NOT_READY",
                $"Service is running but backend pipe did not become ready in 20 seconds. Status={finalStatus}. ServiceName={ServiceMetadata.ServiceName}. Check {RuntimePaths.LogsDirectory}\\bootstrap.log, backend.log, and service-startup.log.");
        }

        return new ServiceRuntimeConvergenceResult(true, "OK", "Running and backend pipe Ping succeeded.");
    }

    internal static ServiceConfigurationSnapshot ReadServiceConfiguration(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
        if (key is null)
        {
            return new ServiceConfigurationSnapshot(
                false,
                serviceName,
                null,
                null,
                null,
                "Missing",
                false,
                false,
                false,
                "Missing",
                GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "<unknown>",
                "<unknown>");
        }

        var imagePath = key.GetValue("ImagePath") as string;
        var serviceExePath = string.IsNullOrWhiteSpace(imagePath) ? null : TryParseExecutablePath(imagePath);
        int? startValue = key.GetValue("Start") is int start ? start : null;
        var delayedAutoStart = key.GetValue("DelayedAutostart") is int delayed && delayed != 0;
        var failureActionsPresent = key.GetValue("FailureActions") is byte[] failureActions && failureActions.Length > 0;
        var failureActionsOnNonCrashFailures = key.GetValue("FailureActionsOnNonCrashFailures") is int failureFlag && failureFlag != 0;
        var configuredStartType = startValue switch
        {
            0 => "Boot",
            1 => "System",
            2 when delayedAutoStart => "DelayedAutomatic",
            2 => "Automatic",
            3 => "Manual",
            4 => "Disabled",
            _ => "Unknown"
        };

        return new ServiceConfigurationSnapshot(
            true,
            serviceName,
            imagePath,
            serviceExePath,
            startValue,
            configuredStartType,
            delayedAutoStart,
            failureActionsPresent,
            failureActionsOnNonCrashFailures,
            GetServiceStatusText(serviceName),
            GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "<unknown>",
            GetPathRoot(serviceExePath) ?? "<unknown>");
    }

    private static void LogServiceLifecycleDiagnostics(
        Action<string> log,
        string? userSid,
        bool requestedStartWithWindows,
        string requestedStartupMode)
    {
        var configuration = ReadServiceConfiguration(ServiceMetadata.ServiceName);
        var actualStartWithWindowsPolicy = IsAutostartEnabledForUser(userSid, log);
        log(
            $"ServiceLifecycleDiagnostics: ServiceName={configuration.ServiceName}, ServiceExePath={configuration.ServiceExePath ?? "<missing>"}, "
            + $"SystemDrive={configuration.SystemDrive}, ServiceExeDrive={configuration.ServiceExeDrive}, RequestedStartupMode={requestedStartupMode}, "
            + $"ConfiguredStartType={configuration.ConfiguredStartType}, DelayedAutoStart={configuration.DelayedAutoStart}, CurrentStatus={configuration.CurrentStatus}, "
            + $"FailureRecoveryConfigured={configuration.FailureRecoveryConfigured}, RequestedStartWithWindows={requestedStartWithWindows}, "
            + $"StartWithWindowsPolicy={actualStartWithWindowsPolicy}, UserSid={userSid ?? "<null>"}.");
    }

    private static string? GetPathRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetPathRoot(path.Trim());
        }
        catch
        {
            return null;
        }
    }

    private static bool PathsReferToSameFile(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAutostartEnabledForUser(string? userSid, Action<string> log)
    {
        RegistryKey? key = null;
        try
        {
            key = string.IsNullOrWhiteSpace(userSid)
                ? Registry.CurrentUser.OpenSubKey(FrontendPolicyKeyPath, writable: false)
                : Registry.Users.OpenSubKey($@"{userSid}\{FrontendPolicyKeyPath}", writable: false);

            var value = key?.GetValue(FrontendPolicyValueName);
            log($"IsAutostartEnabledForUser: Path={(string.IsNullOrWhiteSpace(userSid) ? $@"HKEY_CURRENT_USER\{FrontendPolicyKeyPath}" : $@"HKEY_USERS\{userSid}\{FrontendPolicyKeyPath}")}, ValueName={FrontendPolicyValueName}, RawValue={value ?? "<null>"}.");
            if (value is int intValue)
            {
                return intValue != 0;
            }

            if (value is string stringValue && int.TryParse(stringValue, out var parsed))
            {
                return parsed != 0;
            }

            return false;
        }
        finally
        {
            key?.Dispose();
        }
    }

    private static void SetAutostartPolicyForUser(string? userSid, bool enabled, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(userSid))
        {
            using var key = Registry.CurrentUser.CreateSubKey(FrontendPolicyKeyPath, writable: true);
            key?.SetValue(FrontendPolicyValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
            log($@"SetAutostartPolicyForUser: Path=HKEY_CURRENT_USER\{FrontendPolicyKeyPath}, ValueName={FrontendPolicyValueName}, ValueKind={RegistryValueKind.DWord}, ValueData={(enabled ? 1 : 0)}, Result=Success.");
            return;
        }

        var policyPath = $@"HKEY_USERS\{userSid}\{FrontendPolicyKeyPath}\{FrontendPolicyValueName}";
        try
        {
            using var userRoot = Registry.Users.OpenSubKey(userSid, writable: true)
                ?? throw new InvalidOperationException($"The registry hive for user {userSid} is not loaded.");

            using var key = userRoot.CreateSubKey(FrontendPolicyKeyPath, writable: true)
                ?? throw new InvalidOperationException(
                    $@"Unable to open frontend autostart policy key HKEY_USERS\{userSid}\{FrontendPolicyKeyPath}.");

            key.SetValue(FrontendPolicyValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
            log($@"SetAutostartPolicyForUser: Path=HKEY_USERS\{userSid}\{FrontendPolicyKeyPath}, ValueName={FrontendPolicyValueName}, ValueKind={RegistryValueKind.DWord}, ValueData={(enabled ? 1 : 0)}, Result=Success.");
        }
        catch (Exception ex)
        {
            log($"SetAutostartPolicyForUser: Path={policyPath}, ValueData={(enabled ? 1 : 0)}, Result=Failure, Exception={ex}.");
            throw new InvalidOperationException($"Failed to write {policyPath}: {ex.Message}", ex);
        }
    }

    private static string GetServiceStatusText(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            return service.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            return "Missing";
        }
    }

    private static string TryGetServiceControllerStatusText(ServiceController service)
    {
        try
        {
            service.Refresh();
            return service.Status.ToString();
        }
        catch (Exception ex)
        {
            return $"Unavailable({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static void EnsureKeepFrontendMarker()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(KeepFrontendOnStopMarkerPath, "1");
    }

    private static void TryEnsureKeepFrontendMarker(Action<string> log)
    {
        try
        {
            EnsureKeepFrontendMarker();
            log($"KeepFrontendMarker: Path={KeepFrontendOnStopMarkerPath}, Result=Created.");
        }
        catch (Exception ex)
        {
            log($"KeepFrontendMarker: Path={KeepFrontendOnStopMarkerPath}, Result=Failure, Exception={ex}.");
        }
    }

    private static void TryDeleteKeepFrontendMarker()
    {
        try
        {
            if (File.Exists(KeepFrontendOnStopMarkerPath))
            {
                File.Delete(KeepFrontendOnStopMarkerPath);
            }
        }
        catch
        {
        }
    }

    private static ScResult TryRunSc(Action<string> log, params string[] arguments)
    {
        var stopwatch = Stopwatch.StartNew();
        var startInfo = new ProcessStartInfo
        {
            FileName = "sc.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new ScResult(false, -1, string.Empty, string.Empty, "Failed to start sc.exe.");
        }

        process.WaitForExit();
        stopwatch.Stop();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        log($"RunSc: Arguments={string.Join(" ", arguments)}, ExitCode={process.ExitCode}, ElapsedMs={stopwatch.ElapsedMilliseconds}, Stdout={(process.ExitCode == 0 ? "<suppressed-success>" : stdout)}, Stderr={(process.ExitCode == 0 ? "<suppressed-success>" : stderr)}.");
        if (process.ExitCode == 0)
        {
            return new ScResult(true, process.ExitCode, stdout, stderr, "OK");
        }

        var detail = stderr;
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = stdout;
        }

        var trimmedDetail = string.IsNullOrWhiteSpace(detail)
            ? $"sc.exe exited with code {process.ExitCode}."
            : detail.Trim();
        return new ScResult(false, process.ExitCode, stdout, stderr, trimmedDetail);
    }

    private static DeleteServiceResult TryDeleteServiceRegistration(string serviceName)
    {
        var query = TryOpenService(serviceName, ServiceDelete);
        using var serviceHandle = query.Handle;
        if (!query.Success)
        {
            return query.ErrorCode switch
            {
                ErrorServiceDoesNotExist => new DeleteServiceResult(true, "NOT_INSTALLED", true, false, false, query.ErrorCode, "Service was not installed."),
                ErrorServiceMarkedForDelete => new DeleteServiceResult(true, "SERVICE_PENDING_DELETE", false, true, false, query.ErrorCode, "Service is already marked for deletion."),
                ErrorAccessDenied => new DeleteServiceResult(false, "SERVICE_DELETE_ACCESS_DENIED", false, false, true, query.ErrorCode, query.Detail),
                _ => new DeleteServiceResult(false, "SERVICE_DELETE_FAILED", false, false, true, query.ErrorCode, query.Detail)
            };
        }

        if (DeleteService(serviceHandle!.DangerousGetHandle()))
        {
            return new DeleteServiceResult(true, "SERVICE_DELETE_REQUESTED", false, false, false, 0, "DeleteService succeeded.");
        }

        var error = Marshal.GetLastWin32Error();
        return error switch
        {
            ErrorServiceDoesNotExist => new DeleteServiceResult(true, "NOT_INSTALLED", true, false, false, error, "Service was not installed."),
            ErrorServiceMarkedForDelete => new DeleteServiceResult(true, "SERVICE_PENDING_DELETE", false, true, false, error, "Service is already marked for deletion."),
            ErrorAccessDenied => new DeleteServiceResult(false, "SERVICE_DELETE_ACCESS_DENIED", false, false, true, error, new Win32Exception(error).Message),
            _ => new DeleteServiceResult(false, "SERVICE_DELETE_FAILED", false, false, true, error, new Win32Exception(error).Message)
        };
    }

    private static ServiceRemovalResult WaitForScmRemoval(string serviceName, TimeSpan timeout, Action<string> log)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var query = TryOpenService(serviceName, ServiceQueryStatus);
            query.Handle?.Dispose();
            if (!query.Success)
            {
                if (query.ErrorCode == ErrorServiceDoesNotExist)
                {
                    return new ServiceRemovalResult(true, "REMOVED", "SCM no longer reports the service.", false);
                }

                if (query.ErrorCode == ErrorServiceMarkedForDelete)
                {
                    return new ServiceRemovalResult(false, "SERVICE_PENDING_DELETE", "Service is marked for deletion.", true);
                }
            }

            Thread.Sleep(300);
        }

        var deleteCheck = TryDeleteServiceRegistration(serviceName);
        log($"WaitForScmRemovalDeleteCheck: ServiceName={serviceName}, Code={deleteCheck.Code}, ErrorCode={deleteCheck.ErrorCode}, Detail={deleteCheck.Detail}.");
        if (deleteCheck.PendingDelete)
        {
            return new ServiceRemovalResult(false, "SERVICE_PENDING_DELETE", "Service deletion is pending. Close Services MMC, Task Manager service tab, or any process holding the service handle, then retry; reboot if it remains pending.", true);
        }

        return new ServiceRemovalResult(false, "SERVICE_STILL_PRESENT", "Service still exists in SCM after delete request.", false);
    }

    private static OpenServiceResult TryOpenService(string serviceName, int desiredAccess)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return new OpenServiceResult(false, null, error, $"OpenSCManager failed: {new Win32Exception(error).Message}");
        }

        using var scmHandle = new SafeScHandle(scm);
        var service = OpenService(scmHandle.DangerousGetHandle(), serviceName, desiredAccess);
        if (service == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return new OpenServiceResult(false, null, error, $"OpenService({serviceName}) failed: {new Win32Exception(error).Message}");
        }

        return new OpenServiceResult(true, new SafeScHandle(service), 0, "OK");
    }

    private static bool IsManagedServiceName(string serviceName)
        => string.Equals(serviceName, ServiceMetadata.ServiceName, StringComparison.Ordinal)
           || string.Equals(serviceName, ServiceMetadata.LegacyServiceName, StringComparison.Ordinal);

    private static string? TryParseExecutablePath(string imagePath)
    {
        var trimmed = imagePath.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed[0] == '"')
        {
            var endQuote = trimmed.IndexOf('"', 1);
            return endQuote > 1 ? trimmed[1..endQuote] : null;
        }

        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            return trimmed[..(exeIndex + 4)];
        }

        var firstSpace = trimmed.IndexOf(' ');
        return firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, int desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr serviceControlManager, string serviceName, int desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    private static void WriteResult(string resultFilePath, bool success, string code, string detail)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(resultFilePath) ?? Path.GetTempPath());
        var payload = JsonSerializer.Serialize(new BootstrapResult(success, code, detail), JsonOptions);
        File.WriteAllText(resultFilePath, payload);
    }

    private static bool WaitForBackendPipeReady(TimeSpan timeout, Action<string> log)
    {
        var deadlineUtc = DateTime.UtcNow + timeout;
        var attempt = 0;
        log($"WaitForBackendPipeReadyStart: ServiceName={ServiceMetadata.ServiceName}, TimeoutMs={timeout.TotalMilliseconds}, Status={GetServiceStatusText(ServiceMetadata.ServiceName)}.");
        while (DateTime.UtcNow < deadlineUtc)
        {
            attempt++;
            if (TryPingBackendPipe(TimeSpan.FromSeconds(2), attempt, log))
            {
                log($"WaitForBackendPipeReadyEnd: Result=Success, Attempts={attempt}, Status={GetServiceStatusText(ServiceMetadata.ServiceName)}.");
                return true;
            }

            Thread.Sleep(300);
        }

        log($"WaitForBackendPipeReadyEnd: Result=Timeout, Attempts={attempt}, Status={GetServiceStatusText(ServiceMetadata.ServiceName)}.");
        return false;
    }

    private static bool TryPingBackendPipe(TimeSpan timeout, int attempt, Action<string> log)
    {
        try
        {
            log($"TryPingBackendPipe: Attempt={attempt}, ConnectTimeoutMs={timeout.TotalMilliseconds}, Result=Start.");
            using var pipe = new NamedPipeClientStream(
                ".",
                PipeConstants.PipeName,
                PipeDirection.InOut,
                PipeOptions.None);
            pipe.Connect((int)timeout.TotalMilliseconds);

            using var reader = new StreamReader(
                pipe,
                new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(false),
                leaveOpen: true)
            {
                AutoFlush = true
            };

            var payload = JsonSerializer.Serialize(new PipeEnvelope
            {
                MessageType = PipeMessageType.Request,
                CorrelationId = Guid.NewGuid(),
                Request = new PipeRequest
                {
                    Command = PipeCommand.Ping
                }
            }, JsonOptions);

            writer.WriteLine(payload);
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                log($"TryPingBackendPipe: Attempt={attempt}, Result=FailureEmptyResponse.");
                return false;
            }

            var envelope = JsonSerializer.Deserialize<PipeEnvelope>(line, JsonOptions);
            var success = envelope?.MessageType == PipeMessageType.Response
                   && envelope.Response?.Success == true;
            log($"TryPingBackendPipe: Attempt={attempt}, Result={(success ? "Success" : "FailureResponse")}.");
            return success;
        }
        catch (Exception ex)
        {
            log($"TryPingBackendPipe: Attempt={attempt}, Result=FailureException, ExceptionType={ex.GetType().FullName}, Message={ex.Message}.");
            return false;
        }
    }

    private static string? TryGetArgumentValue(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static UserSidArgument TryGetUserSidArgument(IReadOnlyList<string> args)
    {
        var sid = TryGetArgumentValue(args, "--user-sid");
        if (string.IsNullOrWhiteSpace(sid))
        {
            return new UserSidArgument(true, null, null);
        }

        try
        {
            _ = new SecurityIdentifier(sid);
            return new UserSidArgument(true, sid, null);
        }
        catch (Exception ex)
        {
            return new UserSidArgument(false, null, $"Invalid --user-sid value '{sid}': {ex.Message}");
        }
    }

    private static bool TryParseEnabledArgument(IReadOnlyList<string> args)
    {
        var value = TryGetArgumentValue(args, "--enabled");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string JoinDetails(IEnumerable<string> details, string finalDetail)
        => string.Join(" | ", details.Append(finalDetail).Where(static detail => !string.IsNullOrWhiteSpace(detail)));

    private static bool IsCurrentProcessAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void AppendBootstrapLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BootstrapLogPath) ?? Path.GetTempPath());
            File.AppendAllText(BootstrapLogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private sealed record BootstrapResult(bool Success, string Code, string Detail);

    private sealed record UserSidArgument(bool IsValid, string? Sid, string? Detail);

    private sealed record ServiceHealthResult(bool Healthy, string Reason);

    private sealed record ServiceRemovalResult(bool Success, string Code, string Detail, bool IsPendingDelete);

    private sealed record DeleteServiceResult(
        bool Success,
        string Code,
        bool NotInstalled,
        bool PendingDelete,
        bool Fatal,
        int ErrorCode,
        string Detail);

    private sealed record ScResult(bool Success, int ExitCode, string Stdout, string Stderr, string Detail);

    private sealed record OpenServiceResult(bool Success, SafeScHandle? Handle, int ErrorCode, string Detail);

    private sealed class SafeScHandle : SafeHandle
    {
        public SafeScHandle(IntPtr handle)
            : base(IntPtr.Zero, ownsHandle: true)
        {
            SetHandle(handle);
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    private static void TryEnsureTrayHostViaPipe(Action<string> log)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                PipeConstants.PipeName,
                PipeDirection.InOut,
                PipeOptions.None);
            pipe.Connect(2000);

            using var reader = new StreamReader(
                pipe,
                new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(false),
                leaveOpen: true)
            {
                AutoFlush = true
            };

            var payload = JsonSerializer.Serialize(new PipeEnvelope
            {
                MessageType = PipeMessageType.Request,
                CorrelationId = Guid.NewGuid(),
                Request = new PipeRequest
                {
                    Command = PipeCommand.EnsureTrayHost
                }
            }, JsonOptions);

            writer.WriteLine(payload);
            var line = reader.ReadLine();
            var envelope = string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize<PipeEnvelope>(line, JsonOptions);
            var success = envelope?.MessageType == PipeMessageType.Response
                          && envelope.Response?.Success == true;
            log($"TrayHostReadinessRequest: Result={(success ? "Accepted" : "FailedResponse")}, BackendPipeReady=True.");
        }
        catch (Exception ex)
        {
            // The backend pipe has already passed its required Ping. TrayHost is
            // a separate user-session layer and remains retriable through session
            // events and explicit frontend requests.
            log($"TrayHostReadinessRequest: Result=Failure, BackendPipeReady=True, ExceptionType={ex.GetType().FullName}, Message={ex.Message}.");
        }
    }

    private static BackendServiceStopReason MapRemovalStopReason(string reason)
    {
        if (reason.Contains("UNINSTALL", StringComparison.OrdinalIgnoreCase))
        {
            return BackendServiceStopReason.Uninstall;
        }

        if (reason.Contains("FORCE", StringComparison.OrdinalIgnoreCase))
        {
            return BackendServiceStopReason.ForceRepair;
        }

        return BackendServiceStopReason.InstallRepair;
    }

    private static void TryWriteStopReasonMarker(BackendServiceStopReason reason, Action<string> log)
    {
        try
        {
            ServiceStopReasonMarker.Write(StopReasonMarkerPath, reason);
            log($"ServiceStopReasonMarker: Path={StopReasonMarkerPath}, StopReason={reason}, Result=Created.");
        }
        catch (Exception ex)
        {
            log($"ServiceStopReasonMarker: Path={StopReasonMarkerPath}, StopReason={reason}, Result=Failure, Exception={ex}.");
        }
    }
}
