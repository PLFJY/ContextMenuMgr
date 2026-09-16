using ContextMenuMgr.Backend.Hosting;
using ContextMenuMgr.Backend.Services;

namespace ContextMenuMgr.Backend;

/// <summary>
/// Represents the program.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            BackendEmergencyLogger.Log(
                $"Program.Main started. Args={string.Join(' ', args)}, BaseDirectory={AppContext.BaseDirectory}, UserInteractive={Environment.UserInteractive}, PID={Environment.ProcessId}");

            if (BackendServiceBootstrapper.TryRun(args))
            {
                BackendEmergencyLogger.Log("Program.Main exiting after bootstrap.");
                return 0;
            }

            if (TryRunEnhanceLocalizationSelectionValidation(args, out var localizationSelectionExitCode))
            {
                BackendEmergencyLogger.Log($"Program.Main exiting after enhance localization selection validation. ExitCode={localizationSelectionExitCode}.");
                return localizationSelectionExitCode;
            }

            if (TryRunEnhanceMenuValidation(args, out var validationExitCode))
            {
                BackendEmergencyLogger.Log($"Program.Main exiting after enhance menu validation. ExitCode={validationExitCode}.");
                return validationExitCode;
            }

            BackendEmergencyLogger.Log("Creating BackendRuntime.");
            using var runtime = BackendRuntime.CreateDefault();
            BackendEmergencyLogger.Log("BackendRuntime created.");

            if (BackendWindowsService.ShouldRunAsService(args))
            {
                BackendEmergencyLogger.Log("Running as Windows Service. Entering ServiceBase.Run.");
                using var service = new BackendWindowsService(runtime);
                System.ServiceProcess.ServiceBase.Run(service);
                var exitCode = service.ProcessExitCode;
                BackendEmergencyLogger.Log($"ServiceBase.Run returned. StopReason={service.StopReason}, ExitCode={exitCode}.");
                return exitCode;
            }

            BackendEmergencyLogger.Log("Running in console mode.");
            runtime.RunConsoleAsync(args).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            BackendEmergencyLogger.Log(ex, "Fatal exception in Program.Main.");
            return 1;
        }
    }

    private static bool TryRunEnhanceMenuValidation(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!args.Any(static arg => string.Equals(arg, "--validate-enhance-menus", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var dictionaryPath = TryGetArgumentValue(args, "--dictionary")
            ?? Path.Combine(AppContext.BaseDirectory, "Resources", "EnhanceMenusDic.xml");
        var cultureName = TryGetArgumentValue(args, "--culture");

        exitCode = ContextMenuRegistryCatalog.ValidateEnhanceMenuDictionary(dictionaryPath, cultureName, Console.Out);
        return true;
    }

    private static bool TryRunEnhanceLocalizationSelectionValidation(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!args.Any(static arg => string.Equals(arg, "--validate-enhance-localization-selection", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        exitCode = ContextMenuRegistryCatalog.ValidateEnhanceLocalizationSelection(Console.Out);
        return true;
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
}
