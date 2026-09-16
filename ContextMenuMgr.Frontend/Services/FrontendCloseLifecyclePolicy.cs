namespace ContextMenuMgr.Frontend.Services;

internal readonly record struct FrontendCloseActions(
    bool RequestBackendShutdown,
    bool RequestTrayHostExit);

internal static class FrontendCloseLifecyclePolicy
{
    public static FrontendCloseActions Evaluate(bool keepBackgroundAfterClose, bool autoStartOnLogin)
    {
        // Enabled autostart is a durable user intent. A normal foreground-window
        // close must not stop the only component that can service the next
        // session event and launch TrayHost.
        var stopBackground = !keepBackgroundAfterClose && !autoStartOnLogin;
        return new FrontendCloseActions(stopBackground, stopBackground);
    }
}
