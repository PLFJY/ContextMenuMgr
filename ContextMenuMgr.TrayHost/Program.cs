using System.Runtime.InteropServices;
using ContextMenuMgr.TrayHost;

/// <summary>
/// Represents the program.
/// </summary>
internal static class Program
{
    private const string TrayAppUserModelId = "Context Menu Manager Plus";

    [STAThread]
    private static int Main()
    {
        var logger = new TrayHostLogger();
        try
        {
            TrySetAppUserModelId();
            logger.LogAsync("TrayHost starting.").GetAwaiter().GetResult();
            using var runner = new TrayHostRunner(
                new TrayBackendPipeClient(),
                new FrontendActivationService(AppContext.BaseDirectory),
                logger);
            var exitCode = runner.Run();
            logger.LogAsync($"TrayHost exited normally. ExitCode={exitCode}").GetAwaiter().GetResult();
            return exitCode;
        }
        catch (Exception ex)
        {
            logger.LogAsync($"TrayHost crashed: {ex}").GetAwaiter().GetResult();
            return -1;
        }
    }

    private static void TrySetAppUserModelId()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(TrayAppUserModelId);
        }
        catch
        {
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
}
