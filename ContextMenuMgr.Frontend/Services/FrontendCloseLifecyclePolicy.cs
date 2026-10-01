namespace ContextMenuMgr.Frontend.Services;

internal readonly record struct FrontendCloseActions(
    bool RequestBackendShutdown,
    bool RequestTrayHostExit);

internal static class FrontendCloseLifecyclePolicy
{
    public static FrontendCloseActions Evaluate(bool keepBackgroundAfterClose)
    {
        // Autostart controls the next login; this setting controls the current
        // window close, including when autostart is enabled.
        var stopBackground = !keepBackgroundAfterClose;
        return new FrontendCloseActions(stopBackground, stopBackground);
    }
}
