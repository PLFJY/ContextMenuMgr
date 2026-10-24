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
using static ContextMenuMgr.Backend.Hosting.ServiceScmNative;

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
            using var lifecycle = RuntimePaths.PackageKind == RuntimePackageKind.Portable
                && command is not "repair-runtime-data-acl"
                ? ServiceLifecycleLease.Acquire(TimeSpan.FromSeconds(5)) : null;
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
                "set-startup-mode" => SetServiceStartupMode(TryParseEnabledArgument(args), userSidArgument.Sid, AddDetail,
                    TryGetArgumentValue(args, "--portable-remove-on-exit")),
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
        if (RuntimePaths.PackageKind == RuntimePackageKind.Portable)
        {
            var observed = PortableServiceStartup.WaitForSettledAsync(
                () => PortableServiceStartup.Observe(serviceExePath), TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
            if (observed is PortableServiceRegistrationState.PendingDeletion or PortableServiceRegistrationState.Foreign or PortableServiceRegistrationState.Uncertain)
                return (false, observed == PortableServiceRegistrationState.PendingDeletion ? "SERVICE_PENDING_DELETE" : "SERVICE_IMAGE_PATH_MISMATCH",
                    "Portable service is pending deletion or ownership is uncertain. Close Services MMC and retry after the previous runtime exits; do not repair another installation.");
        }
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
            var createResult = RunScm(log, "CreateService", () => CreateOwnService(binaryPath, createStartupMode));
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
                        createResult = RunScm(log, "CreateService", () => CreateOwnService(binaryPath, createStartupMode));
                    }
                    else
                    {
                        var configExistingResult = RunScm(log, "ImagePath", () => ConfigureOwnImagePath(binaryPath));
                        if (!configExistingResult.Success)
                        {
                            return (false, "SERVICE_CONFIG_FAILED", configExistingResult.Detail);
                        }

                        createResult = new ServiceCommandResult(true, 0, "Existing healthy service will be configured.");
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
            var configResult = RunScm(log, "ImagePath", () => ConfigureOwnImagePath(binaryPath));
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

        var descriptionResult = RunScm(log, "Description", () => ConfigureOwnDescription("Context Menu Manager Plus elevated backend service"));
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
                    return RunScm(log, "StartupMode", () => ConfigureOwnStartupMode(startupMode));
                },
                readConfiguration: () => ReadServiceConfiguration(ServiceMetadata.ServiceName),
                configureRecovery: () =>
                {
                    var result = ConfigureAndVerifyServiceRecovery(log);
                    return new ServiceCommandResult(result.Success, result.Success ? 0 : -1, result.Detail);
                },
                convergeRuntime: () => ConvergeServiceRuntime(serviceExePath, log),
                commitPolicy: () => SetAutostartPolicyForUser(userSid, isAutostartEnabled, log));

            log($"ServiceStartupModeResult: RequestedStartupMode={startupMode}, ActualStartupMode={transition.ActualStartupMode}, Win32Error={transition.Win32Error}, ServiceStatus={transition.ServiceStatus}, PolicyCommitted={transition.PolicyCommitted}, Success={transition.Success}, Code={transition.Code}.");
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

        if (RuntimePaths.PackageKind == RuntimePackageKind.Portable && !persistRequestedPolicyOnSuccess
            && PortableServiceExitPolicy.HasUnsettledStartupTransaction(RuntimePaths.DataDirectory))
        {
            if (string.IsNullOrWhiteSpace(userSid)) return (false, "USER_SID_REQUIRED", "Explicit user context is required to recover a Portable startup transaction.");
            using var paths = new PortableRuntimeIdentity();
            paths.PinDirectory(RuntimePaths.DataDirectory);
            // A verified explicit repair adopts the effective autostart policy
            // and retains the registration until the user opts into cleanup again.
            FrontendSettingsFile.UpdateLifecycle(RuntimePaths.SettingsPath, isAutostartEnabled, removeOnExit: false);
            File.Delete(Path.Combine(RuntimePaths.DataDirectory, ServiceMetadata.PortableStartupTransactionMarkerFileName));
            if (PortableServiceExitPolicy.HasUnsettledStartupTransaction(RuntimePaths.DataDirectory))
                return (false, "PORTABLE_STARTUP_ROLLBACK_FAILED", "Transaction marker removal could not be verified after repair.");
            log("PortableStartupTransactionRecovered: cleanup disabled after verified service repair.");
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

    private static (bool Success, string Code, string Detail) SetServiceStartupMode(
        bool enabled, string? userSid, Action<string> log, string? removeOnExit)
    {
        if (RuntimePaths.PackageKind != RuntimePackageKind.Portable)
            return SetServiceStartupModeCore(enabled, userSid, log);
        if (string.IsNullOrWhiteSpace(userSid) || removeOnExit is not (null or "0" or "1"))
            return (false, "INVALID_PORTABLE_STARTUP_POLICY", "An explicit user SID and boolean cleanup policy are required.");

        using var paths = new PortableRuntimeIdentity();
        paths.PinDirectory(RuntimePaths.RootDirectory);
        using var settings = File.OpenRead(RuntimePaths.SettingsPath);
        var previousPolicy = PortableServiceExitPolicy.Read(settings);
        settings.Dispose();
        var desiredRemoval = !enabled && (removeOnExit is null ? previousPolicy.RemoveOnExit : removeOnExit == "1");
        var previousConfiguration = ReadServiceConfiguration(ServiceMetadata.ServiceName);
        if (previousConfiguration.Exists && !string.Equals(previousConfiguration.ImagePath?.Trim(),
                $"\"{Environment.ProcessPath}\" --service", StringComparison.OrdinalIgnoreCase))
            return (false, "SERVICE_IMAGE_PATH_MISMATCH", "Another installation owns the service registration.");
        if (previousConfiguration.Exists && previousConfiguration.StartValue is not (2 or 3 or 4))
            return (false, "SERVICE_STARTUP_CONFIG_MISMATCH", "Previous startup mode is uncertain.");

        using var userRoot = Registry.Users.OpenSubKey(userSid, writable: true)
            ?? throw new IOException("Authenticated user's hive is not loaded.");
        using var policy = userRoot.CreateSubKey(FrontendPolicyKeyPath, writable: true);
        using var run = userRoot.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        var oldValue = policy.GetValue(FrontendPolicyValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var oldKind = oldValue is null ? RegistryValueKind.DWord : policy.GetValueKind(FrontendPolicyValueName);
        var oldRun = run?.GetValue("ContextMenuManagerPlus.TrayHost", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var oldRunKind = oldRun is null ? RegistryValueKind.String : run!.GetValueKind("ContextMenuManagerPlus.TrayHost");
        var result = PortableStartupTransaction.Execute(
            RuntimePaths.DataDirectory,
            () =>
            {
                var transition = SetServiceStartupModeCore(enabled, userSid, log);
                return new(transition.Success, transition.Code, transition.Detail);
            },
            () => FrontendSettingsFile.UpdateLifecycle(RuntimePaths.SettingsPath, enabled, desiredRemoval, expected: previousPolicy),
            () =>
            {
                // Roll back in the same elevated process; never ask for a second
                // UAC authorization to compensate for a failed transaction.
                var current = ReadServiceConfiguration(ServiceMetadata.ServiceName);
                if (current.Exists)
                {
                    if (!PathsReferToSameFile(current.ServiceExePath, Environment.ProcessPath))
                        throw new IOException("Service ownership changed; rollback refused.");
                    var oldMode = previousConfiguration.StartValue switch
                    {
                        2 when previousConfiguration.DelayedAutoStart => "delayed-auto",
                        2 => "auto", 4 => "disabled", _ => "demand"
                    };
                    var alreadyRestored = current.StartValue == (previousConfiguration.StartValue ?? 3)
                        && (current.StartValue != 2 || current.DelayedAutoStart == previousConfiguration.DelayedAutoStart);
                    if (!alreadyRestored)
                    {
                        if (!current.MatchesRequestedStartupMode(GetRequestedStartupMode(enabled, Environment.ProcessPath!)))
                            throw new IOException("External SCM startup change preserved; rollback is uncertain.");
                        var restored = RunScm(log, "StartupModeRollback", () => ConfigureOwnStartupMode(oldMode));
                        var observed = ReadServiceConfiguration(ServiceMetadata.ServiceName);
                        if (!restored.Success || observed.StartValue != (previousConfiguration.StartValue ?? 3)
                            || observed.StartValue == 2 && observed.DelayedAutoStart != previousConfiguration.DelayedAutoStart)
                            throw new IOException("SCM startup rollback could not be verified.");
                    }
                }
                var actual = policy.GetValue(FrontendPolicyValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (!Equals(actual, oldValue) && !Equals(actual, enabled ? 1 : 0))
                    throw new IOException("External user policy change preserved; rollback is uncertain.");
                if (oldValue is null) policy.DeleteValue(FrontendPolicyValueName, false);
                else policy.SetValue(FrontendPolicyValueName, oldValue, oldKind);
                if (oldRun is not null && run?.GetValue("ContextMenuManagerPlus.TrayHost") is null)
                    run?.SetValue("ContextMenuManagerPlus.TrayHost", oldRun, oldRunKind);
                if (!Equals(policy.GetValue(FrontendPolicyValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames), oldValue)
                    || oldValue is not null && policy.GetValueKind(FrontendPolicyValueName) != oldKind
                    || !Equals(run?.GetValue("ContextMenuManagerPlus.TrayHost", null, RegistryValueOptions.DoNotExpandEnvironmentNames), oldRun)
                    || oldRun is not null && run!.GetValueKind("ContextMenuManagerPlus.TrayHost") != oldRunKind)
                    throw new IOException("User policy rollback read-back failed.");
                using var stream = File.OpenRead(RuntimePaths.SettingsPath);
                var actualSettings = PortableServiceExitPolicy.Read(stream);
                stream.Dispose();
                if (actualSettings.AutoStart == enabled && actualSettings.RemoveOnExit == desiredRemoval)
                    FrontendSettingsFile.UpdateLifecycle(RuntimePaths.SettingsPath, previousPolicy.AutoStart, previousPolicy.RemoveOnExit, expected: actualSettings);
                else if (actualSettings.AutoStart != previousPolicy.AutoStart || actualSettings.RemoveOnExit != previousPolicy.RemoveOnExit)
                    throw new IOException("External lifecycle settings change preserved; rollback is uncertain.");
            });
        log($"PortableStartupTransaction: Success={result.Success}, Code={result.Code}, Detail={result.Detail}.");
        return (result.Success, result.Code, result.Detail);
    }

    private static (bool Success, string Code, string Detail) SetServiceStartupModeCore(bool enabled, string? userSid, Action<string> log)
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
                return RunScm(log, "StartupMode", () => ConfigureOwnStartupMode("demand"));
            },
            readConfiguration: () => ReadServiceConfiguration(ServiceMetadata.ServiceName),
            configureRecovery: static () => new ServiceCommandResult(true, 0, "Not required while disabling autostart."),
            convergeRuntime: static () => new ServiceRuntimeConvergenceResult(true, "OK", "Not required while disabling autostart."),
            commitPolicy: () => SetAutostartPolicyForUser(userSid, enabled: false, log));

        log($"ServiceStartupModeResult: RequestedStartupMode=demand, ActualStartupMode={transition.ActualStartupMode}, Win32Error={transition.Win32Error}, ServiceStatus={transition.ServiceStatus}, PolicyCommitted={transition.PolicyCommitted}, Success={transition.Success}, Code={transition.Code}.");
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
            var disableFailureFlag = RunScm(log, "RemovalRecoverySuppression", () => DisableRecoveryDuringRemoval(serviceName));
            log($"RemovalRecoverySuppression: ServiceName={serviceName}, Success={disableFailureFlag.Success}, Win32Error={disableFailureFlag.ExitCode}. Normal SCM stop remains non-recoverable even if this best-effort guard fails.");
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

    private static (bool Success, string Code, string Detail) ConfigureAndVerifyStartupMode(
        string requestedStartupMode,
        Action<string> log)
    {
        var configurationResult = RunScm(log, "StartupMode", () => ConfigureOwnStartupMode(requestedStartupMode));
        var configuration = ReadServiceConfiguration(ServiceMetadata.ServiceName);
        log($"ServiceStartupModeResult: RequestedStartupMode={requestedStartupMode}, ActualStartupMode={configuration.ConfiguredStartType}, DelayedAutoStart={configuration.DelayedAutoStart}, Win32Error={configurationResult.ExitCode}, ServiceStatus={configuration.CurrentStatus}.");

        if (!configurationResult.Success)
        {
            return (false, "SERVICE_STARTUP_CONFIG_FAILED", configurationResult.Detail);
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
        // The native operation performs exact actions/reset/flag read-back on
        // the same service handle. No diagnostic child process is required.
        var result = RunScm(log, "Recovery", ConfigureOwnRecovery);
        log($"ServiceRecoveryConfiguration: FailureRecoveryConfigured={result.Success}, Win32Error={result.ExitCode}, ResetSeconds={ServiceRecoveryConfiguration.RequiredResetSeconds}, Actions={RecoveryActions}, Detail={result.Detail}.");
        return result.Success ? (true, "OK", RecoveryActions) : (false, "SERVICE_RECOVERY_CONFIG_FAILED", result.Detail);
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
        ScmServiceConfiguration native;
        try { native = ServiceScmNative.ReadConfiguration(serviceName); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorServiceDoesNotExist)
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
                false,
                "Missing",
                GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "<unknown>",
                "<unknown>");
        }

        var imagePath = native.ImagePath;
        var serviceExePath = string.IsNullOrWhiteSpace(imagePath) ? null : TryParseExecutablePath(imagePath);
        int? startValue = checked((int)native.StartType);
        var delayedAutoStart = native.DelayedAutoStart;
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
            native.Recovery.Actions.Count > 0,
            native.Recovery.HasExpectedActions,
            native.Recovery.OnNonCrashFailures,
            Enum.IsDefined(typeof(ServiceControllerStatus), (int)native.State)
                ? ((ServiceControllerStatus)native.State).ToString() : "Unknown",
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
            if (RuntimePaths.PackageKind == RuntimePackageKind.Portable)
            {
                using var run = userRoot.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                run?.DeleteValue("ContextMenuManagerPlus.TrayHost", false);
                key.Flush();
                if (key.GetValue(FrontendPolicyValueName) is not int actual || actual != (enabled ? 1 : 0)
                    || run?.GetValue("ContextMenuManagerPlus.TrayHost") is not null)
                    throw new IOException("User startup policy read-back failed.");
            }
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

    private static ServiceCommandResult RunScm(Action<string> log, string operation, Func<string> execute)
    {
        var stopwatch = Stopwatch.StartNew();
        ServiceCommandResult result;
        try { result = new(true, 0, execute()); }
        catch (Exception ex)
        {
            result = new(false, ex is Win32Exception native ? native.NativeErrorCode : -1, ex.Message);
        }
        log($"ScmOperation: Operation={operation}, Success={result.Success}, Win32Error={result.ExitCode}, ElapsedMs={stopwatch.Elapsed.TotalMilliseconds:F2}, Detail={result.Detail}.");
        return result;
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
            log($"TryPingBackendPipe: Attempt={attempt}, RequestTimeoutMs={timeout.TotalMilliseconds}, Result=Start.");
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var pipe = new NamedPipeClientStream(
                ".",
                PipeConstants.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            pipe.ConnectAsync(timeoutCts.Token).GetAwaiter().GetResult();

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

            writer.WriteLineAsync(payload.AsMemory(), timeoutCts.Token).GetAwaiter().GetResult();
            var line = reader.ReadLineAsync(timeoutCts.Token).AsTask().GetAwaiter().GetResult();
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
        => RuntimeLogFiles.WriteEmergency("bootstrap.log",
            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [PID={Environment.ProcessId}] {message}{Environment.NewLine}");

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

    private sealed record OpenServiceResult(bool Success, SafeScHandle? Handle, int ErrorCode, string Detail);

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
