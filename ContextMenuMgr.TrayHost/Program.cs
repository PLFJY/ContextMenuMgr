using ContextMenuMgr.TrayHost;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        using var runner = new TrayHostRunner(
            new TrayBackendPipeClient(),
            new FrontendActivationService(AppContext.BaseDirectory),
            new TrayHostLogger());
        return runner.Run();
    }
}
