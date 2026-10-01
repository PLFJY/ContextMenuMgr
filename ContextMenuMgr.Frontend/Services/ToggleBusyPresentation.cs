using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Lets a backend-backed toggle's busy visual reach a rendered frame before
/// starting work that may synchronously occupy the UI thread.
/// </summary>
public static class ToggleBusyPresentation
{
    public static async Task WaitForFirstFrameAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || !dispatcher.CheckAccess())
        {
            await Task.Yield();
            return;
        }

        var rendering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (++frames >= 2) rendering.TrySetResult();
        };
        CompositionTarget.Rendering += handler;
        try
        {
            // A minimized or hidden window may not render. Never hold a registry
            // operation indefinitely just to wait for an animation frame.
            await Task.WhenAny(rendering.Task, Task.Delay(100));
        }
        finally
        {
            CompositionTarget.Rendering -= handler;
        }

        // Rendering fires before WPF completes the frame. Resume after its
        // dispatcher work rather than inside the Rendering callback.
        await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }
}
