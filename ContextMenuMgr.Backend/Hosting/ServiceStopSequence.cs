namespace ContextMenuMgr.Backend.Hosting;

// Serializes deletion-before-stop with SCM stop / shutdown callbacks. The stop
// callback always runs, even when the best-effort deletion fails or throws.
internal sealed class ServiceStopSequence
{
    private readonly Lock _gate = new();
    private bool _requested, _stopped, _cleanupAttempted;

    public void Request(Action? cleanup, Action requestStop)
    {
        lock (_gate)
        {
            if (_requested || _stopped) return;
            _requested = true;
            try { CleanupOnce(cleanup); }
            finally { requestStop(); }
        }
    }

    public void Stop(Action? cleanup, Action stopCore)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            try { CleanupOnce(cleanup); }
            finally { stopCore(); }
        }
    }

    private void CleanupOnce(Action? cleanup)
    {
        if (cleanup is null || _cleanupAttempted) return;
        _cleanupAttempted = true;
        cleanup();
    }
}
