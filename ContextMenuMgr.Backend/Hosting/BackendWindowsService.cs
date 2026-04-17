using System.ServiceProcess;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Hosting;

// The backend executable can run interactively for development, or as a
// Windows Service when installed by the frontend bootstrapper.
public sealed class BackendWindowsService : ServiceBase
{
    private readonly BackendRuntime _runtime;
    private CancellationTokenSource? _serviceCts;

    public BackendWindowsService(BackendRuntime runtime)
    {
        _runtime = runtime;
        ServiceName = ServiceMetadata.ServiceName;
        CanStop = true;
        AutoLog = true;
        CanHandleSessionChangeEvent = true;
    }

    public static bool ShouldRunAsService(string[] args) =>
        args.Any(static arg => string.Equals(arg, "--service", StringComparison.OrdinalIgnoreCase)) ||
        !Environment.UserInteractive;

    protected override void OnStart(string[] args)
    {
        _serviceCts = new CancellationTokenSource();
        _runtime.StopRequested += OnRuntimeStopRequested;
        _ = _runtime.StartAsync(_serviceCts.Token);
    }

    protected override void OnStop()
    {
        _runtime.StopRequested -= OnRuntimeStopRequested;
        _serviceCts?.Cancel();
        _runtime.StopAsync().GetAwaiter().GetResult();
        _serviceCts?.Dispose();
        _serviceCts = null;
    }

    private void OnRuntimeStopRequested(object? sender, EventArgs e)
    {
        _ = Task.Run(() =>
        {
            try
            {
                Stop();
            }
            catch
            {
            }
        });
    }

}
