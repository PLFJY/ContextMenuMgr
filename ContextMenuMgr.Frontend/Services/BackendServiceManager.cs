using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

public sealed class BackendServiceManager : IBackendServiceManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsServiceInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceMetadata.ServiceName}");
        var installed = key is not null;
        FrontendDebugLog.Info("BackendServiceManager", $"IsServiceInstalled -> {installed}");
        return installed;
    }

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

    public async Task<BackendServiceBootstrapResult> InstallOrRepairServiceAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        FrontendDebugLog.Info("BackendServiceManager", "InstallOrRepairServiceAsync started.");
        var backendExePath = ResolveBackendExecutablePath();
        if (backendExePath is null)
        {
            FrontendDebugLog.Info("BackendServiceManager", "Backend executable path not found.");
            return new BackendServiceBootstrapResult(false, false, "BACKEND_EXE_MISSING", string.Empty);
        }
        FrontendDebugLog.Info("BackendServiceManager", $"Resolved backend executable path: {backendExePath}");

        var resultFilePath = Path.Combine(
            Path.GetTempPath(),
            $"ContextMenuMgr-bootstrap-{Guid.NewGuid():N}.json");
        var script = BuildInstallScript(backendExePath, resultFilePath);
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedScript}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(startInfo);
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
            FrontendDebugLog.Error("BackendServiceManager", ex, "User cancelled UAC elevation.");
            return new BackendServiceBootstrapResult(false, true, "ELEVATION_CANCELLED", string.Empty);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("BackendServiceManager", ex, "InstallOrRepairServiceAsync threw.");
            return new BackendServiceBootstrapResult(false, false, "BOOTSTRAP_EXCEPTION", ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(resultFilePath))
                {
                    File.Delete(resultFilePath);
                }
            }
            catch
            {
            }
        }
    }

    public async Task<BackendServiceBootstrapResult> UninstallServiceAsync(CancellationToken cancellationToken)
    {
        var resultFilePath = Path.Combine(
            Path.GetTempPath(),
            $"ContextMenuMgr-uninstall-{Guid.NewGuid():N}.json");
        var script = BuildUninstallScript(resultFilePath);
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedScript}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new BackendServiceBootstrapResult(false, false, "FAILED_TO_START_ELEVATED_PROCESS", string.Empty);
            }

            await process.WaitForExitAsync(cancellationToken);
            var scriptResult = await TryReadScriptResultAsync(resultFilePath, cancellationToken);

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
            return new BackendServiceBootstrapResult(false, true, "ELEVATION_CANCELLED", string.Empty);
        }
        catch (Exception ex)
        {
            return new BackendServiceBootstrapResult(false, false, "BOOTSTRAP_EXCEPTION", ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(resultFilePath))
                {
                    File.Delete(resultFilePath);
                }
            }
            catch
            {
            }
        }
    }

    public async Task<BackendServiceBootstrapResult> StopServiceAsync(CancellationToken cancellationToken)
    {
        var resultFilePath = Path.Combine(
            Path.GetTempPath(),
            $"ContextMenuMgr-stop-{Guid.NewGuid():N}.json");
        var script = BuildStopScript(resultFilePath);
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedScript}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new BackendServiceBootstrapResult(false, false, "FAILED_TO_START_ELEVATED_PROCESS", string.Empty);
            }

            await process.WaitForExitAsync(cancellationToken);
            var scriptResult = await TryReadScriptResultAsync(resultFilePath, cancellationToken);

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
            return new BackendServiceBootstrapResult(false, true, "ELEVATION_CANCELLED", string.Empty);
        }
        catch (Exception ex)
        {
            return new BackendServiceBootstrapResult(false, false, "BOOTSTRAP_EXCEPTION", ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(resultFilePath))
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
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ContextMenuManagerPlus.Service.exe"),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "artifacts", "backend",
#if DEBUG
                "Debug",
#else
                "Release",
#endif
                "net10.0-windows",
                "ContextMenuManagerPlus.Service.exe"))
        };

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

        await using var stream = File.OpenRead(resultFilePath);
        return await JsonSerializer.DeserializeAsync<BootstrapScriptResult>(stream, JsonOptions, cancellationToken);
    }

    private static string BuildInstallScript(string backendExePath, string resultFilePath)
    {
        var quotedBackendPath = $"\"{backendExePath}\" --service";

        return
            "$ErrorActionPreference = 'Stop'\n" +
            $"$serviceName = '{ServiceMetadata.ServiceName}'\n" +
            $"$displayName = '{ServiceMetadata.DisplayName}'\n" +
            $"$binaryPath = '{quotedBackendPath.Replace("'", "''")}'\n" +
            $"$resultFile = '{resultFilePath.Replace("'", "''")}'\n" +
            "$dataDir = Join-Path $env:ProgramData 'ContextMenuMgr\\Data'\n" +
            $"$markerFile = Join-Path $dataDir '{ServiceMetadata.KeepFrontendOnStopMarkerFileName}'\n" +
            "\n" +
            "function Write-Result($success, $code, $detail) {\n" +
            "    $payload = @{ Success = $success; Code = $code; Detail = $detail }\n" +
            "    $payload | ConvertTo-Json -Compress | Set-Content -Path $resultFile -Encoding UTF8\n" +
            "}\n" +
            "\n" +
            "function Ensure-KeepFrontendMarker() {\n" +
            "    New-Item -Path $dataDir -ItemType Directory -Force | Out-Null\n" +
            "    Set-Content -Path $markerFile -Value '1' -Encoding ASCII\n" +
            "}\n" +
            "\n" +
            "function Wait-ForServiceStatus($name, $desiredStatus, $timeoutSeconds) {\n" +
            "    $deadline = (Get-Date).AddSeconds($timeoutSeconds)\n" +
            "    do {\n" +
            "        $service = Get-Service -Name $name -ErrorAction SilentlyContinue\n" +
            "        if ($null -eq $service) {\n" +
            "            return $false\n" +
            "        }\n" +
            "\n" +
            "        if ($service.Status.ToString() -eq $desiredStatus) {\n" +
            "            return $true\n" +
            "        }\n" +
            "\n" +
            "        Start-Sleep -Milliseconds 300\n" +
            "    } while ((Get-Date) -lt $deadline)\n" +
            "\n" +
            "    return $false\n" +
            "}\n" +
            "\n" +
            "function Test-ServiceRegistrationHealthy($name) {\n" +
            "    $serviceKeyPath = \"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\$name\"\n" +
            "    if (-not (Test-Path $serviceKeyPath)) {\n" +
            "        return $false\n" +
            "    }\n" +
            "\n" +
            "    $serviceKey = Get-ItemProperty -Path $serviceKeyPath -ErrorAction SilentlyContinue\n" +
            "    if ($null -eq $serviceKey) {\n" +
            "        return $false\n" +
            "    }\n" +
            "\n" +
            "    return -not [string]::IsNullOrWhiteSpace($serviceKey.ImagePath) -and $null -ne $serviceKey.Start -and $null -ne $serviceKey.Type\n" +
            "}\n" +
            "\n" +
            "function Remove-ServiceRegistration($name) {\n" +
            "    $service = Get-Service -Name $name -ErrorAction SilentlyContinue\n" +
            "    if ($null -ne $service -and $service.Status -ne 'Stopped') {\n" +
            "        Ensure-KeepFrontendMarker\n" +
            "        Stop-Service -Name $name -Force -ErrorAction SilentlyContinue\n" +
            "        [void](Wait-ForServiceStatus $name 'Stopped' 10)\n" +
            "    }\n" +
            "\n" +
            "    sc.exe delete $name | Out-Null\n" +
            "    $deadline = (Get-Date).AddSeconds(10)\n" +
            "    do {\n" +
            "        Start-Sleep -Milliseconds 300\n" +
            "    } while ((Get-Date) -lt $deadline -and (Test-Path \"HKLM:\\SYSTEM\\CurrentControlSet\\Services\\$name\"))\n" +
            "}\n" +
            "\n" +
            "try {\n" +
            "    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue\n" +
            "    if ($null -ne $service -and -not (Test-ServiceRegistrationHealthy $serviceName)) {\n" +
            "        Remove-ServiceRegistration $serviceName\n" +
            "        $service = $null\n" +
            "    }\n" +
            "\n" +
            $"    $legacyServiceName = '{ServiceMetadata.LegacyServiceName}'\n" +
            "    if ($serviceName -ne $legacyServiceName) {\n" +
            "        $legacyService = Get-Service -Name $legacyServiceName -ErrorAction SilentlyContinue\n" +
            "        if ($null -ne $legacyService) {\n" +
            "            Remove-ServiceRegistration $legacyServiceName\n" +
            "        }\n" +
            "    }\n" +
            "\n" +
            "    if ($null -eq $service) {\n" +
            "        New-Service -Name $serviceName -DisplayName $displayName -BinaryPathName $binaryPath -StartupType Automatic | Out-Null\n" +
            "    }\n" +
            "    else {\n" +
            "        if ($service.Status -ne 'Stopped') {\n" +
            "            Ensure-KeepFrontendMarker\n" +
            "            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue\n" +
            "            [void](Wait-ForServiceStatus $serviceName 'Stopped' 10)\n" +
            "        }\n" +
            "\n" +
            "        sc.exe config $serviceName start= auto | Out-Null\n" +
            "        sc.exe config $serviceName binPath= $binaryPath | Out-Null\n" +
            "    }\n" +
            "\n" +
            "    if (-not (Test-ServiceRegistrationHealthy $serviceName)) {\n" +
            "        throw \"Service registration is incomplete after repair.\"\n" +
            "    }\n" +
            "\n" +
            "    sc.exe description $serviceName \"Context Menu Manager Plus elevated backend service\" | Out-Null\n" +
            "\n" +
            "    $service = Get-Service -Name $serviceName -ErrorAction Stop\n" +
            "    if ($service.Status -ne 'Running') {\n" +
            "        Start-Service -Name $serviceName -ErrorAction Stop\n" +
            "    }\n" +
            "\n" +
            "    if (-not (Wait-ForServiceStatus $serviceName 'Running' 10)) {\n" +
            "        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue\n" +
            "        $status = if ($null -eq $service) { 'Missing' } else { $service.Status.ToString() }\n" +
            "        Write-Result $false 'SERVICE_NOT_RUNNING' $status\n" +
            "        exit 2\n" +
            "    }\n" +
            "\n" +
            "    Write-Result $true 'OK' 'Running'\n" +
            "    exit 0\n" +
            "}\n" +
            "catch {\n" +
            "    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue\n" +
            "    $status = if ($null -eq $service) { 'Missing' } else { $service.Status.ToString() }\n" +
            "    Write-Result $false 'SERVICE_BOOTSTRAP_ERROR' ($_.Exception.Message + ' | Status=' + $status)\n" +
            "    exit 1\n" +
            "}\n";
    }

    private static string BuildUninstallScript(string resultFilePath)
    {
        return
            "$ErrorActionPreference = 'Stop'\n" +
            $"$serviceName = '{ServiceMetadata.ServiceName}'\n" +
            $"$resultFile = '{resultFilePath.Replace("'", "''")}'\n" +
            $"$dataDir = Join-Path $env:ProgramData 'ContextMenuMgr\\Data'\n" +
            $"$markerFile = Join-Path $dataDir '{ServiceMetadata.KeepFrontendOnStopMarkerFileName}'\n" +
            "\n" +
            "function Write-Result($success, $code, $detail) {\n" +
            "    $payload = @{ Success = $success; Code = $code; Detail = $detail }\n" +
            "    $payload | ConvertTo-Json -Compress | Set-Content -Path $resultFile -Encoding UTF8\n" +
            "}\n" +
            "\n" +
            "try {\n" +
            "    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue\n" +
            "    if ($null -eq $service) {\n" +
            "        Write-Result $true 'NOT_INSTALLED' 'Service was not installed.'\n" +
            "        exit 0\n" +
            "    }\n" +
            "\n" +
            "    if ($service.Status -ne 'Stopped') {\n" +
            "        New-Item -Path $dataDir -ItemType Directory -Force | Out-Null\n" +
            "        Set-Content -Path $markerFile -Value '1' -Encoding ASCII\n" +
            "        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue\n" +
            "        Start-Sleep -Seconds 1\n" +
            "    }\n" +
            "\n" +
            "sc.exe delete $serviceName | Out-Null\n" +
            "    if (Test-Path $markerFile) { Remove-Item -Path $markerFile -Force -ErrorAction SilentlyContinue }\n" +
            "    Write-Result $true 'UNINSTALLED' 'Service removed.'\n" +
            "    exit 0\n" +
            "}\n" +
            "catch {\n" +
            "    if (Test-Path $markerFile) { Remove-Item -Path $markerFile -Force -ErrorAction SilentlyContinue }\n" +
            "    Write-Result $false 'SERVICE_UNINSTALL_ERROR' $_.Exception.Message\n" +
            "    exit 1\n" +
            "}\n";
    }

    private static string BuildStopScript(string resultFilePath)
    {
        return
            "$ErrorActionPreference = 'Stop'\n" +
            $"$serviceName = '{ServiceMetadata.ServiceName}'\n" +
            $"$resultFile = '{resultFilePath.Replace("'", "''")}'\n" +
            "\n" +
            "function Write-Result($success, $code, $detail) {\n" +
            "    $payload = @{ Success = $success; Code = $code; Detail = $detail }\n" +
            "    $payload | ConvertTo-Json -Compress | Set-Content -Path $resultFile -Encoding UTF8\n" +
            "}\n" +
            "\n" +
            "function Wait-ForServiceStatus($name, $desiredStatus, $timeoutSeconds) {\n" +
            "    $deadline = (Get-Date).AddSeconds($timeoutSeconds)\n" +
            "    do {\n" +
            "        $service = Get-Service -Name $name -ErrorAction SilentlyContinue\n" +
            "        if ($null -eq $service) {\n" +
            "            return $desiredStatus -eq 'Stopped'\n" +
            "        }\n" +
            "\n" +
            "        if ($service.Status.ToString() -eq $desiredStatus) {\n" +
            "            return $true\n" +
            "        }\n" +
            "\n" +
            "        Start-Sleep -Milliseconds 300\n" +
            "    } while ((Get-Date) -lt $deadline)\n" +
            "\n" +
            "    return $false\n" +
            "}\n" +
            "\n" +
            "try {\n" +
            "    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue\n" +
            "    if ($null -eq $service) {\n" +
            "        Write-Result $true 'NOT_INSTALLED' 'Service was not installed.'\n" +
            "        exit 0\n" +
            "    }\n" +
            "\n" +
            "    if ($service.Status -eq 'Stopped') {\n" +
            "        Write-Result $true 'ALREADY_STOPPED' 'Stopped'\n" +
            "        exit 0\n" +
            "    }\n" +
            "\n" +
            "    Stop-Service -Name $serviceName -Force -ErrorAction Stop\n" +
            "\n" +
            "    if (-not (Wait-ForServiceStatus $serviceName 'Stopped' 10)) {\n" +
            "        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue\n" +
            "        $status = if ($null -eq $service) { 'Missing' } else { $service.Status.ToString() }\n" +
            "        Write-Result $false 'SERVICE_NOT_STOPPED' $status\n" +
            "        exit 2\n" +
            "    }\n" +
            "\n" +
            "    Write-Result $true 'STOPPED' 'Stopped'\n" +
            "    exit 0\n" +
            "}\n" +
            "catch {\n" +
            "    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue\n" +
            "    $status = if ($null -eq $service) { 'Missing' } else { $service.Status.ToString() }\n" +
            "    Write-Result $false 'SERVICE_STOP_ERROR' ($_.Exception.Message + ' | Status=' + $status)\n" +
            "    exit 1\n" +
            "}\n";
    }

    private sealed record BootstrapScriptResult(bool Success, string Code, string Detail);
}
