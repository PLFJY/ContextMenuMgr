using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Hosting;

internal static class BackendServiceBootstrapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string FrontendPolicyKeyPath = @"Software\ContextMenuMgr\Frontend";
    private const string FrontendPolicyValueName = "StartWithWindows";
    private static readonly string DataDirectory = RuntimePaths.DataDirectory;
    private static readonly string KeepFrontendOnStopMarkerPath = Path.Combine(
        DataDirectory,
        ServiceMetadata.KeepFrontendOnStopMarkerFileName);

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
            return true;
        }

        var result = Execute(command, args);
        WriteResult(resultFilePath, result.Success, result.Code, result.Detail);
        Environment.ExitCode = result.Success ? 0 : 1;
        return true;
    }

    private static (bool Success, string Code, string Detail) Execute(string command, IReadOnlyList<string> args)
    {
        try
        {
            return command.ToLowerInvariant() switch
            {
                "install-or-repair" => InstallOrRepairService(),
                "uninstall" => UninstallService(),
                "stop" => StopService(),
                "set-startup-mode" => SetServiceStartupMode(TryParseEnabledArgument(args)),
                _ => (false, "UNKNOWN_BOOTSTRAP_COMMAND", command)
            };
        }
        catch (Exception ex)
        {
            var status = GetServiceStatusText(ServiceMetadata.ServiceName);
            return (false, "SERVICE_BOOTSTRAP_ERROR", $"{ex.Message} | Status={status}");
        }
    }

    private static (bool Success, string Code, string Detail) InstallOrRepairService()
    {
        var serviceExePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(serviceExePath) || !File.Exists(serviceExePath))
        {
            return (false, "BACKEND_EXE_MISSING", string.Empty);
        }

        var binaryPath = $"\"{serviceExePath}\" --service";
        var startupMode = IsAutostartEnabledForCurrentUser() ? "auto" : "demand";

        if (ServiceExists(ServiceMetadata.ServiceName) && !TestServiceRegistrationHealthy(ServiceMetadata.ServiceName))
        {
            RemoveServiceRegistration(ServiceMetadata.ServiceName, keepFrontendAlive: true);
        }

        if (ServiceExists(ServiceMetadata.LegacyServiceName))
        {
            RemoveServiceRegistration(ServiceMetadata.LegacyServiceName, keepFrontendAlive: true);
        }

        if (!ServiceExists(ServiceMetadata.ServiceName))
        {
            RunSc(
                "create",
                ServiceMetadata.ServiceName,
                "binPath=",
                binaryPath,
                "start=",
                startupMode,
                "DisplayName=",
                ServiceMetadata.DisplayName);
        }
        else
        {
            using var service = new ServiceController(ServiceMetadata.ServiceName);
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                EnsureKeepFrontendMarker();
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }

            RunSc(
                "config",
                ServiceMetadata.ServiceName,
                "binPath=",
                binaryPath,
                "start=",
                startupMode);
        }

        if (!TestServiceRegistrationHealthy(ServiceMetadata.ServiceName))
        {
            return (false, "SERVICE_REGISTRATION_INCOMPLETE", string.Empty);
        }

        RunSc("description", ServiceMetadata.ServiceName, "Context Menu Manager Plus elevated backend service");

        using (var service = new ServiceController(ServiceMetadata.ServiceName))
        {
            if (service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            }
        }

        var status = GetServiceStatusText(ServiceMetadata.ServiceName);
        return string.Equals(status, nameof(ServiceControllerStatus.Running), StringComparison.OrdinalIgnoreCase)
            ? (true, "OK", "Running")
            : (false, "SERVICE_NOT_RUNNING", status);
    }

    private static (bool Success, string Code, string Detail) UninstallService()
    {
        if (!ServiceExists(ServiceMetadata.ServiceName))
        {
            TryDeleteKeepFrontendMarker();
            return (true, "NOT_INSTALLED", "Service was not installed.");
        }

        RemoveServiceRegistration(ServiceMetadata.ServiceName, keepFrontendAlive: true);
        TryDeleteKeepFrontendMarker();
        return (true, "UNINSTALLED", "Service removed.");
    }

    private static (bool Success, string Code, string Detail) StopService()
    {
        if (!ServiceExists(ServiceMetadata.ServiceName))
        {
            return (true, "NOT_INSTALLED", "Service was not installed.");
        }

        using var service = new ServiceController(ServiceMetadata.ServiceName);
        if (service.Status == ServiceControllerStatus.Stopped)
        {
            return (true, "ALREADY_STOPPED", "Stopped");
        }

        service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
        var status = GetServiceStatusText(ServiceMetadata.ServiceName);
        return string.Equals(status, nameof(ServiceControllerStatus.Stopped), StringComparison.OrdinalIgnoreCase)
            ? (true, "STOPPED", "Stopped")
            : (false, "SERVICE_NOT_STOPPED", status);
    }

    private static (bool Success, string Code, string Detail) SetServiceStartupMode(bool enabled)
    {
        if (!ServiceExists(ServiceMetadata.ServiceName))
        {
            return (true, "NOT_INSTALLED", "Service was not installed.");
        }

        RunSc(
            "config",
            ServiceMetadata.ServiceName,
            "start=",
            enabled ? "auto" : "demand");

        return (true, enabled ? "STARTUP_AUTO" : "STARTUP_MANUAL", enabled ? "Automatic" : "Manual");
    }

    private static void RemoveServiceRegistration(string serviceName, bool keepFrontendAlive)
    {
        if (ServiceExists(serviceName))
        {
            using var service = new ServiceController(serviceName);
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                if (keepFrontendAlive)
                {
                    EnsureKeepFrontendMarker();
                }

                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
        }

        RunSc("delete", serviceName);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && ServiceExists(serviceName))
        {
            Thread.Sleep(300);
        }
    }

    private static bool ServiceExists(string serviceName)
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

    private static bool TestServiceRegistrationHealthy(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
        if (key is null)
        {
            return false;
        }

        var imagePath = key.GetValue("ImagePath") as string;
        var start = key.GetValue("Start");
        var type = key.GetValue("Type");
        return !string.IsNullOrWhiteSpace(imagePath) && start is not null && type is not null;
    }

    private static bool IsAutostartEnabledForCurrentUser()
    {
        using var key = Registry.CurrentUser.OpenSubKey(FrontendPolicyKeyPath, writable: false);
        var value = key?.GetValue(FrontendPolicyValueName);
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

    private static void EnsureKeepFrontendMarker()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(KeepFrontendOnStopMarkerPath, "1");
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

    private static void RunSc(params string[] arguments)
    {
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
            throw new InvalidOperationException("Failed to start sc.exe.");
        }

        process.WaitForExit();
        if (process.ExitCode == 0)
        {
            return;
        }

        var detail = process.StandardError.ReadToEnd();
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = process.StandardOutput.ReadToEnd();
        }

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
            ? $"sc.exe exited with code {process.ExitCode}."
            : detail.Trim());
    }

    private static void WriteResult(string resultFilePath, bool success, string code, string detail)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(resultFilePath) ?? Path.GetTempPath());
        var payload = JsonSerializer.Serialize(new BootstrapResult(success, code, detail), JsonOptions);
        File.WriteAllText(resultFilePath, payload);
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

    private static bool TryParseEnabledArgument(IReadOnlyList<string> args)
    {
        var value = TryGetArgumentValue(args, "--enabled");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record BootstrapResult(bool Success, string Code, string Detail);
}
