using ContextMenuMgr.Backend.Hosting;

namespace ContextMenuMgr.Backend;

/// <summary>
/// Represents the program.
/// </summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        if (BackendServiceBootstrapper.TryRun(args))
        {
            return;
        }

        using var runtime = BackendRuntime.CreateDefault();

        if (BackendWindowsService.ShouldRunAsService(args))
        {
            System.ServiceProcess.ServiceBase.Run(new BackendWindowsService(runtime));
            return;
        }

        runtime.RunConsoleAsync(args).GetAwaiter().GetResult();
    }
}
