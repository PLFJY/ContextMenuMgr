using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the backend Service Manager.
/// </summary>
public sealed class BackendServiceManager : IBackendServiceManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PortablePackageTrustService _portablePackageTrustService;

    public BackendServiceManager(PortablePackageTrustService portablePackageTrustService)
    {
        _portablePackageTrustService = portablePackageTrustService;
    }

    /// <summary>
    /// Executes is Service Installed.
    /// </summary>
    public bool IsServiceInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceMetadata.ServiceName}");
        var installed = key is not null;
        FrontendDebugLog.Info("BackendServiceManager", $"IsServiceInstalled -> {installed}");
        return installed;
    }

    /// <summary>
    /// Gets service Status.
    /// </summary>
    public ServiceControllerStatus? GetServiceStatus()
    {
        try
        {
            using var service = new ServiceController(ServiceMetadata.ServiceName);
            _ = service.Status;
            var status = service.Status;
            FrontendDebugLog.Info("BackendServiceManager", $"GetServiceStatus -> {status}");
            return status;
        }
        catch (InvalidOperationException)
        {
            FrontendDebugLog.Info("BackendServiceManager", "GetServiceStatus -> Missing");
            return null;
        }
    }

    /// <summary>
    /// Executes install Or Repair Service Async.
    /// </summary>
    public async Task<BackendServiceBootstrapResult> InstallOrRepairServiceAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        FrontendDebugLog.Info("BackendServiceManager", "InstallOrRepairServiceAsync started.");
        FrontendDebugLog.Info("BackendServiceManager", $"Current frontend SID: {GetCurrentUserSid() ?? "<null>"}");
        var backendExePath = ResolveBackendExecutablePath();
        if (backendExePath is null)
        {
            FrontendDebugLog.Info("BackendServiceManager", "Backend executable path not found.");
            return new BackendServiceBootstrapResult(false, false, "BACKEND_EXE_MISSING", string.Empty);
        }
        FrontendDebugLog.Info("BackendServiceManager", $"Resolved backend executable path: {backendExePath}");

        var trustReport = await _portablePackageTrustService.ScanPortableRuntimeFilesAsync(cancellationToken);
        if (trustReport.BlockedCount > 0)
        {
            var blockedFileNames = string.Join(
                ", ",
                trustReport.BlockedFiles.Select(static file => file.RelativePath));
            FrontendDebugLog.Warning(
                "BackendServiceManager",
                "ServiceBootstrapBlockedByMotw: "
                + $"PackageKind={RuntimePaths.PackageKind}, "
                + $"BaseDirectory={AppContext.BaseDirectory}, "
                + $"BlockedFiles={blockedFileNames}, "
                + "ResultCode=PORTABLE_RUNTIME_FILES_BLOCKED.");
            return new BackendServiceBootstrapResult(
                false,
                false,
                "PORTABLE_RUNTIME_FILES_BLOCKED",
                blockedFileNames);
        }

        var resultFilePath = Path.Combine(
            Path.GetTempPath(),
            $"ContextMenuMgr-bootstrap-{Guid.NewGuid():N}.json");
        try
        {
            var bootstrapArguments = AppendUserSidArgument($"--service-bootstrap install-or-repair --result-file \"{resultFilePath}\"");
            FrontendDebugLog.Info("BackendServiceManager", $"Install bootstrap arguments: {bootstrapArguments}, HasUserSid={bootstrapArguments.Contains("--user-sid", StringComparison.OrdinalIgnoreCase)}");
            using var process = Process.Start(CreateElevatedBackendStartInfo(
                backendExePath,
                bootstrapArguments));
            if (process is null)
            {
                FrontendDebugLog.Info("BackendServiceManager", "Failed to start elevated bootstrap process.");
                return new BackendServiceBootstrapResult(false, false, "FAILED_TO_START_ELEVATED_PROCESS", string.Empty);
            }
            FrontendDebugLog.Info("BackendServiceManager", $"Elevated bootstrap process started. PID={process.Id}, ResultFile={resultFilePath}");

            await process.WaitForExitAsync(cancellationToken);
            FrontendDebugLog.Info("BackendServiceManager", $"Elevated bootstrap process exited. ExitCode={process.ExitCode}, Elapsed={stopwatch.ElapsedMilliseconds} ms");
            var scriptResult = await TryReadScriptResultAsync(resultFilePath, cancellationToken);
            FrontendDebugLog.Info("BackendServiceManager", $"Bootstrap result file: Success={scriptResult?.Success}, Code={scriptResult?.Code}, Detail={scriptResult?.Detail}");

            if (process.ExitCode == 0 && scriptResult?.Success == true)
            {
                return new BackendServiceBootstrapResult(true, false, scriptResult.Code, scriptResult.Detail);
            }

            return new BackendServiceBootstrapResult(
                false,
                false,
                scriptResult?.Code ?? $"EXIT_CODE_{process.ExitCode}",
                scriptResult?.Detail ?? string.Empty);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            FrontendDebugLog.Error("BackendServiceManager", ex, $"UAC cancelled during install. Elapsed={stopwatch.ElapsedMilliseconds} ms.");
            return new BackendServiceBootstrapResult(false, true, "ELEVATION_CANCELLED", string.Empty);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("BackendServiceManager", ex, $"InstallOrRepairServiceAsync threw. Exception={ex}, Elapsed={stopwatch.ElapsedMilliseconds} ms.");
            return new BackendServiceBootstrapResult(false, false, "BOOTSTRAP_EXCEPTION", ex.ToString());
        }
        finally
        {
            try
            {
                var exists = File.Exists(resultFilePath);
                FrontendDebugLog.Info("BackendServiceManager", $"Install result file exists before deletion: {exists}, Path={resultFilePath}");
                if (exists)
                {
                    File.Delete(resultFilePath);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Executes uninstall Service Async.
    /// </summary>
    public async Task<BackendServiceBootstrapResult> UninstallServiceAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        FrontendDebugLog.Info("BackendServiceManager", "UninstallServiceAsync started.");
        var backendExePath = ResolveBackendExecutablePath();
        if (backendExePath is null)
        {
            return new BackendServiceBootstrapResult(false, false, "BACKEND_EXE_MISSING", string.Empty);
        }

        var resultFilePath = Path.Combine(
            Path.GetTempPath(),
            $"ContextMenuMgr-uninstall-{Guid.NewGuid():N}.json");

        try
        {
            var bootstrapArguments = $"--service-bootstrap uninstall --result-file \"{resultFilePath}\"";
            FrontendDebugLog.Info("BackendServiceManager", $"Uninstall bootstrap arguments: {bootstrapArguments}, HasUserSid={bootstrapArguments.Contains("--user-sid", StringComparison.OrdinalIgnoreCase)}, ResultFile={resultFilePath}");
            using var process = Process.Start(CreateElevatedBackendStartInfo(
                backendExePath,
                bootstrapArguments));
            if (process is null)
            {
                return new BackendServiceBootstrapResult(false, false, "FAILED_TO_START_ELEVATED_PROCESS", string.Empty);
            }

            FrontendDebugLog.Info("BackendServiceManager", $"Uninstall bootstrap process started. PID={process.Id}");
            await process.WaitForExitAsync(cancellationToken);
            FrontendDebugLog.Info("BackendServiceManager", $"Uninstall bootstrap process exited. ExitCode={process.ExitCode}, Elapsed={stopwatch.ElapsedMilliseconds} ms");
            var scriptResult = await TryReadScriptResultAsync(resultFilePath, cancellationToken);
            FrontendDebugLog.Info("BackendServiceManager", $"Uninstall parsed result: Success={scriptResult?.Success}, Code={scriptResult?.Code}, Detail={scriptResult?.Detail}");

            if (process.ExitCode == 0 && scriptResult?.Success == true)
            {
                return new BackendServiceBootstrapResult(true, false, scriptResult.Code, scriptResult.Detail);
            }

            return new BackendServiceBootstrapResult(
                false,
                false,
                scriptResult?.Code ?? $"EXIT_CODE_{process.ExitCode}",
                scriptResult?.Detail ?? string.Empty);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            FrontendDebugLog.Error("BackendServiceManager", ex, $"UAC cancelled during uninstall. Elapsed={stopwatch.ElapsedMilliseconds} ms.");
            return new BackendServiceBootstrapResult(false, true, "ELEVATION_CANCELLED", string.Empty);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("BackendServiceManager", ex, $"UninstallServiceAsync threw. Exception={ex}, Elapsed={stopwatch.ElapsedMilliseconds} ms.");
            return new BackendServiceBootstrapResult(false, false, "BOOTSTRAP_EXCEPTION", ex.ToString());
        }
        finally
        {
            try
            {
                var exists = File.Exists(resultFilePath);
                FrontendDebugLog.Info("BackendServiceManager", $"Uninstall result file exists before deletion: {exists}, Path={resultFilePath}");
                if (exists)
                {
                    File.Delete(resultFilePath);
                }
            }
            catch
            {
            }
        }
    }

    public async Task<BackendServiceBootstrapResult> ForceRemoveServiceAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        FrontendDebugLog.Info("BackendServiceManager", "ForceRemoveServiceAsync started.");
        var backendExePath = ResolveBackendExecutablePath();
        if (backendExePath is null)
        {
            return new BackendServiceBootstrapResult(false, false, "BACKEND_EXE_MISSING", string.Empty);
        }

        var resultFilePath = Path.Combine(
            Path.GetTempPath(),
            $"ContextMenuMgr-forceremove-{Guid.NewGuid():N}.json");

        try
        {
            var bootstrapArguments = $"--service-bootstrap force-remove-service --result-file \"{resultFilePath}\"";
            FrontendDebugLog.Info("BackendServiceManager", $"Force remove bootstrap arguments: {bootstrapArguments}, ResultFile={resultFilePath}");
            using var process = Process.Start(CreateElevatedBackendStartInfo(
                backendExePath,
                bootstrapArguments));
            if (process is null)
            {
                return new BackendServiceBootstrapResult(false, false, "FAILED_TO_START_ELEVATED_PROCESS", string.Empty);
            }

            FrontendDebugLog.Info("BackendServiceManager", $"Force remove bootstrap process started. PID={process.Id}");
            await process.WaitForExitAsync(cancellationToken);
            FrontendDebugLog.Info("BackendServiceManager", $"Force remove bootstrap process exited. ExitCode={process.ExitCode}, Elapsed={stopwatch.ElapsedMilliseconds} ms");
            var scriptResult = await TryReadScriptResultAsync(resultFilePath, cancellationToken);
            FrontendDebugLog.Info("BackendServiceManager", $"Force remove parsed result: Success={scriptResult?.Success}, Code={scriptResult?.Code}, Detail={scriptResult?.Detail}");

            if (process.ExitCode == 0 && scriptResult?.Success == true)
            {
                return new BackendServiceBootstrapResult(true, false, scriptResult.Code, scriptResult.Detail);
            }

            return new BackendServiceBootstrapResult(
                false,
                false,
                scriptResult?.Code ?? $"EXIT_CODE_{process.ExitCode}",
                scriptResult?.Detail ?? string.Empty);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            FrontendDebugLog.Error("BackendServiceManager", ex, $"UAC cancelled during force remove. Elapsed={stopwatch.ElapsedMilliseconds} ms.");
            return new BackendServiceBootstrapResult(false, true, "ELEVATION_CANCELLED", string.Empty);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("BackendServiceManager", ex, $"ForceRemoveServiceAsync threw. Exception={ex}, Elapsed={stopwatch.ElapsedMilliseconds} ms.");
            return new BackendServiceBootstrapResult(false, false, "BOOTSTRAP_EXCEPTION", ex.ToString());
        }
        finally
        {
            try
            {
                var exists = File.Exists(resultFilePath);
                FrontendDebugLog.Info("BackendServiceManager", $"Force remove result file exists before deletion: {exists}, Path={resultFilePath}");
                if (exists)
                {
                    File.Delete(resultFilePath);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Stops service Async.
    /// </summary>
    public async Task<BackendServiceBootstrapResult> StopServiceAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        FrontendDebugLog.Info("BackendServiceManager", "StopServiceAsync started.");
        var backendExePath = ResolveBackendExecutablePath();
        if (backendExePath is null)
        {
            return new BackendServiceBootstrapResult(false, false, "BACKEND_EXE_MISSING", string.Empty);
        }

        var resultFilePath = Path.Combine(
            Path.GetTempPath(),
            $"ContextMenuMgr-stop-{Guid.NewGuid():N}.json");

        try
        {
            var bootstrapArguments = $"--service-bootstrap stop --result-file \"{resultFilePath}\"";
            FrontendDebugLog.Info("BackendServiceManager", $"Stop bootstrap arguments: {bootstrapArguments}, HasUserSid={bootstrapArguments.Contains("--user-sid", StringComparison.OrdinalIgnoreCase)}, ResultFile={resultFilePath}");
            using var process = Process.Start(CreateElevatedBackendStartInfo(
                backendExePath,
                bootstrapArguments));
            if (process is null)
            {
                return new BackendServiceBootstrapResult(false, false, "FAILED_TO_START_ELEVATED_PROCESS", string.Empty);
            }

            FrontendDebugLog.Info("BackendServiceManager", $"Stop bootstrap process started. PID={process.Id}");
            await process.WaitForExitAsync(cancellationToken);
            FrontendDebugLog.Info("BackendServiceManager", $"Stop bootstrap process exited. ExitCode={process.ExitCode}, Elapsed={stopwatch.ElapsedMilliseconds} ms");
            var scriptResult = await TryReadScriptResultAsync(resultFilePath, cancellationToken);
            FrontendDebugLog.Info("BackendServiceManager", $"Stop parsed result: Success={scriptResult?.Success}, Code={scriptResult?.Code}, Detail={scriptResult?.Detail}");

            if (process.ExitCode == 0 && scriptResult?.Success == true)
            {
                return new BackendServiceBootstrapResult(true, false, scriptResult.Code, scriptResult.Detail);
            }

            return new BackendServiceBootstrapResult(
                false,
                false,
                scriptResult?.Code ?? $"EXIT_CODE_{process.ExitCode}",
                scriptResult?.Detail ?? string.Empty);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            FrontendDebugLog.Error("BackendServiceManager", ex, $"UAC cancelled during stop. Elapsed={stopwatch.ElapsedMilliseconds} ms.");
            return new BackendServiceBootstrapResult(false, true, "ELEVATION_CANCELLED", string.Empty);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("BackendServiceManager", ex, $"StopServiceAsync threw. Exception={ex}, Elapsed={stopwatch.ElapsedMilliseconds} ms.");
            return new BackendServiceBootstrapResult(false, false, "BOOTSTRAP_EXCEPTION", ex.ToString());
        }
        finally
        {
            try
            {
                var exists = File.Exists(resultFilePath);
                FrontendDebugLog.Info("BackendServiceManager", $"Stop result file exists before deletion: {exists}, Path={resultFilePath}");
                if (exists)
                {
                    File.Delete(resultFilePath);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Sets service Auto Start Enabled Async.
    /// </summary>
    public async Task<BackendServiceBootstrapResult> SetServiceAutoStartEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        FrontendDebugLog.Info("BackendServiceManager", $"SetServiceAutoStartEnabledAsync started. Enabled={enabled}, CurrentSid={GetCurrentUserSid() ?? "<null>"}");
        var backendExePath = ResolveBackendExecutablePath();
        if (backendExePath is null)
        {
            return new BackendServiceBootstrapResult(false, false, "BACKEND_EXE_MISSING", string.Empty);
        }

        var resultFilePath = Path.Combine(
            Path.GetTempPath(),
            $"ContextMenuMgr-startupmode-{Guid.NewGuid():N}.json");

        try
        {
            FrontendDebugLog.Info("BackendServiceManager", $"Resolved backend executable path: {backendExePath}");
            FrontendDebugLog.Info("BackendServiceManager", $"Startup mode result file path: {resultFilePath}");
            var bootstrapArguments = AppendUserSidArgument($"--service-bootstrap set-startup-mode --enabled {(enabled ? "1" : "0")} --result-file \"{resultFilePath}\"");
            FrontendDebugLog.Info("BackendServiceManager", $"Startup mode bootstrap arguments: {bootstrapArguments}, HasUserSid={bootstrapArguments.Contains("--user-sid", StringComparison.OrdinalIgnoreCase)}");
            using var process = Process.Start(CreateElevatedBackendStartInfo(
                backendExePath,
                bootstrapArguments));
            if (process is null)
            {
                FrontendDebugLog.Info("BackendServiceManager", "Failed to start elevated startup mode bootstrap process.");
                return new BackendServiceBootstrapResult(false, false, "FAILED_TO_START_ELEVATED_PROCESS", string.Empty);
            }

            FrontendDebugLog.Info("BackendServiceManager", $"Startup mode bootstrap process started. PID={process.Id}");
            await process.WaitForExitAsync(cancellationToken);
            FrontendDebugLog.Info("BackendServiceManager", $"Startup mode bootstrap process exited. ExitCode={process.ExitCode}, Elapsed={stopwatch.ElapsedMilliseconds} ms");
            var scriptResult = await TryReadScriptResultAsync(resultFilePath, cancellationToken);
            FrontendDebugLog.Info("BackendServiceManager", $"Startup mode parsed result: Success={scriptResult?.Success}, Code={scriptResult?.Code}, Detail={scriptResult?.Detail}");

            if (process.ExitCode == 0 && scriptResult?.Success == true)
            {
                return new BackendServiceBootstrapResult(true, false, scriptResult.Code, scriptResult.Detail);
            }

            return new BackendServiceBootstrapResult(
                false,
                false,
                scriptResult?.Code ?? $"EXIT_CODE_{process.ExitCode}",
                scriptResult?.Detail ?? string.Empty);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            FrontendDebugLog.Error("BackendServiceManager", ex, $"UAC cancelled during startup mode change. Elapsed={stopwatch.ElapsedMilliseconds} ms.");
            return new BackendServiceBootstrapResult(false, true, "ELEVATION_CANCELLED", string.Empty);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("BackendServiceManager", ex, $"SetServiceAutoStartEnabledAsync threw. Exception={ex}, Elapsed={stopwatch.ElapsedMilliseconds} ms.");
            return new BackendServiceBootstrapResult(false, false, "BOOTSTRAP_EXCEPTION", ex.ToString());
        }
        finally
        {
            try
            {
                var exists = File.Exists(resultFilePath);
                FrontendDebugLog.Info("BackendServiceManager", $"Startup mode result file exists before deletion: {exists}, Path={resultFilePath}");
                if (exists)
                {
                    File.Delete(resultFilePath);
                }
            }
            catch
            {
            }
        }
    }

    public async Task<BackendServiceBootstrapResult> RepairRuntimeDataAclAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        FrontendDebugLog.Info("BackendServiceManager", "RepairRuntimeDataAclAsync started.");
        var backendExePath = ResolveBackendExecutablePath();
        if (backendExePath is null)
        {
            FrontendDebugLog.Info("BackendServiceManager", "Backend executable path not found for runtime data ACL repair.");
            return new BackendServiceBootstrapResult(false, false, "BACKEND_EXE_MISSING", string.Empty);
        }

        var resultFilePath = Path.Combine(
            Path.GetTempPath(),
            $"ContextMenuMgr-aclrepair-{Guid.NewGuid():N}.json");

        try
        {
            var bootstrapArguments = $"--service-bootstrap repair-runtime-data-acl --result-file \"{resultFilePath}\"";
            FrontendDebugLog.Info("BackendServiceManager", $"Runtime data ACL repair bootstrap arguments: {bootstrapArguments}, ResultFile={resultFilePath}");
            using var process = Process.Start(CreateElevatedBackendStartInfo(
                backendExePath,
                bootstrapArguments));
            if (process is null)
            {
                FrontendDebugLog.Info("BackendServiceManager", "Failed to start elevated runtime data ACL repair bootstrap process.");
                return new BackendServiceBootstrapResult(false, false, "FAILED_TO_START_ELEVATED_PROCESS", string.Empty);
            }

            FrontendDebugLog.Info("BackendServiceManager", $"Runtime data ACL repair bootstrap process started. PID={process.Id}");
            await process.WaitForExitAsync(cancellationToken);
            FrontendDebugLog.Info("BackendServiceManager", $"Runtime data ACL repair bootstrap process exited. ExitCode={process.ExitCode}, Elapsed={stopwatch.ElapsedMilliseconds} ms");
            var scriptResult = await TryReadScriptResultAsync(resultFilePath, cancellationToken);
            FrontendDebugLog.Info("BackendServiceManager", $"Runtime data ACL repair parsed result: Success={scriptResult?.Success}, Code={scriptResult?.Code}, Detail={scriptResult?.Detail}");

            if (process.ExitCode == 0 && scriptResult?.Success == true)
            {
                return new BackendServiceBootstrapResult(true, false, scriptResult.Code, scriptResult.Detail);
            }

            return new BackendServiceBootstrapResult(
                false,
                false,
                scriptResult?.Code ?? $"EXIT_CODE_{process.ExitCode}",
                scriptResult?.Detail ?? string.Empty);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            FrontendDebugLog.Error("BackendServiceManager", ex, $"UAC cancelled during runtime data ACL repair. Elapsed={stopwatch.ElapsedMilliseconds} ms.");
            return new BackendServiceBootstrapResult(false, true, "ELEVATION_CANCELLED", string.Empty);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("BackendServiceManager", ex, $"RepairRuntimeDataAclAsync threw. Exception={ex}, Elapsed={stopwatch.ElapsedMilliseconds} ms.");
            return new BackendServiceBootstrapResult(false, false, "BOOTSTRAP_EXCEPTION", ex.ToString());
        }
        finally
        {
            try
            {
                var exists = File.Exists(resultFilePath);
                FrontendDebugLog.Info("BackendServiceManager", $"Runtime data ACL repair result file exists before deletion: {exists}, Path={resultFilePath}");
                if (exists)
                {
                    File.Delete(resultFilePath);
                }
            }
            catch
            {
            }
        }
    }

    private static string? ResolveBackendExecutablePath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ContextMenuManagerPlus.Service.exe")
        };

        foreach (var configuration in new[] { "Beta", "Release", "Debug" })
        {
            candidates.Add(Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "artifacts", "backend",
                configuration,
                "net10.0-windows",
                "ContextMenuManagerPlus.Service.exe")));
        }

        foreach (var candidate in candidates)
        {
            FrontendDebugLog.Info("BackendServiceManager", $"Checking backend executable candidate: {candidate}");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<BootstrapScriptResult?> TryReadScriptResultAsync(
        string resultFilePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(resultFilePath))
        {
            FrontendDebugLog.Info("BackendServiceManager", $"Bootstrap result file does not exist: {resultFilePath}");
            return null;
        }

        var rawContent = await File.ReadAllTextAsync(resultFilePath, cancellationToken);
        FrontendDebugLog.Info("BackendServiceManager", $"Bootstrap result raw content: {rawContent}");
        return JsonSerializer.Deserialize<BootstrapScriptResult>(rawContent, JsonOptions);
    }

    private static ProcessStartInfo CreateElevatedBackendStartInfo(string backendExePath, string arguments)
    {
        return new ProcessStartInfo
        {
            FileName = backendExePath,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(backendExePath) ?? AppContext.BaseDirectory
        };
    }

    private static string? GetCurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string AppendUserSidArgument(string arguments)
    {
        var sid = GetCurrentUserSid();
        if (string.IsNullOrWhiteSpace(sid))
        {
            FrontendDebugLog.Info("BackendServiceManager", "AppendUserSidArgument: current SID missing; --user-sid omitted.");
            return arguments;
        }

        FrontendDebugLog.Info("BackendServiceManager", $"AppendUserSidArgument: --user-sid present for SID={sid}.");
        return $"{arguments} --user-sid \"{sid}\"";
    }

    private sealed record BootstrapScriptResult(bool Success, string Code, string Detail);
}
