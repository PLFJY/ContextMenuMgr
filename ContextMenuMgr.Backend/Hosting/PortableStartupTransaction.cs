using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Hosting;

internal static class PortableStartupTransaction
{
    public static ServiceRuntimeConvergenceResult Execute(
        string dataDirectory, Func<ServiceRuntimeConvergenceResult> transition, Action commitSettings, Action rollback)
    {
        if (PortableServiceExitPolicy.HasUnsettledStartupTransaction(dataDirectory))
            return new(false, "PORTABLE_STARTUP_ROLLBACK_FAILED", "An earlier Portable startup transaction is uncertain. Run explicit service repair first.");
        var marker = Path.Combine(dataDirectory, ServiceMetadata.PortableStartupTransactionMarkerFileName);
        // Existence is a veto, including after a process crash or partial write.
        using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) stream.Flush(flushToDisk: true);
        ServiceRuntimeConvergenceResult result;
        try
        {
            result = transition();
            if (result.Success) commitSettings();
        }
        catch (Exception ex) { result = new(false, "PORTABLE_STARTUP_COMMIT_FAILED", ex.Message); }
        if (!result.Success)
        {
            try { rollback(); }
            catch (Exception ex)
            {
                return new(false, "PORTABLE_STARTUP_ROLLBACK_FAILED", $"{result.Code}: {result.Detail}; rollback: {ex.Message}");
            }
        }
        try
        {
            File.Delete(marker);
            if (PortableServiceExitPolicy.HasUnsettledStartupTransaction(dataDirectory))
                throw new IOException("Transaction marker removal could not be verified.");
        }
        catch (Exception ex) { return new(false, "PORTABLE_STARTUP_ROLLBACK_FAILED", $"The transaction marker could not be cleared: {ex.Message}"); }
        return result;
    }
}
