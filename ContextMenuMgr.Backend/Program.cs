using ContextMenuMgr.Backend.Hosting;

namespace ContextMenuMgr.Backend;

internal static class Program
{
    private static void Main(string[] args)
    {
        using var runtime = BackendRuntime.CreateDefault();

        if (BackendWindowsService.ShouldRunAsService(args))
        {
            System.ServiceProcess.ServiceBase.Run(new BackendWindowsService(runtime));
            return;
        }

        runtime.RunConsoleAsync(args).GetAwaiter().GetResult();
    }
}
