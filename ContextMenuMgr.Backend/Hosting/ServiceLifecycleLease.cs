namespace ContextMenuMgr.Backend.Hosting;

// Bootstrapper changes and self-removal must not interleave. This is used only
// by SYSTEM / elevated lifecycle processes, never by an impersonated client.
internal sealed class ServiceLifecycleLease : IDisposable
{
    private readonly Mutex _mutex;
    private ServiceLifecycleLease(Mutex mutex) => _mutex = mutex;

    public static ServiceLifecycleLease Acquire(TimeSpan timeout)
    {
        var mutex = new Mutex(false, @"Global\ContextMenuManagerPlus.ServiceLifecycle");
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("Another service lifecycle operation is in progress.");
            return new ServiceLifecycleLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
