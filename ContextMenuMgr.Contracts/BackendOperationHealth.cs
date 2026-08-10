namespace ContextMenuMgr.Contracts;

/// <summary>
/// Distinguishes pipe/service reachability from a failure of one backend operation.
/// </summary>
public enum BackendOperationHealth
{
    ServiceUnavailable,
    OperationFailed
}

/// <summary>
/// Provides the small, shared reachability classification used by frontend health UX.
/// </summary>
public static class BackendOperationHealthClassifier
{
    public static BackendOperationHealth FromPingResult(bool pingSucceeded)
        => pingSucceeded ? BackendOperationHealth.OperationFailed : BackendOperationHealth.ServiceUnavailable;
}
