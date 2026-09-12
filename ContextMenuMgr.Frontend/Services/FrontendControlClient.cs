using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the frontend Control Client.
/// </summary>
public static class FrontendControlClient
{
    /// <summary>
    /// Attempts to send Async.
    /// </summary>
    public static async Task<bool> TrySendAsync(FrontendControlRequest request, CancellationToken cancellationToken)
        => await FrontendControlPipeClient.TrySendAsync(request, cancellationToken).ConfigureAwait(false);

    internal static async Task<bool> TrySendAsync(
        FrontendControlRequest request,
        string pipeName,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
        => await FrontendControlPipeClient
            .TrySendAsync(request, pipeName, requestTimeout, cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<bool> TrySendWithStartupRetryAsync(
        FrontendControlRequest request,
        CancellationToken cancellationToken,
        Action<int, bool>? attemptCompleted = null)
        => await FrontendControlPipeClient
            .TrySendWithStartupRetryAsync(
                request,
                PipeConstants.FrontendControlPipeName,
                FrontendControlPipeClient.DefaultStartupGracePeriod,
                FrontendControlPipeClient.DefaultRequestTimeout,
                FrontendControlPipeClient.DefaultRetryDelay,
                cancellationToken,
                attemptCompleted)
            .ConfigureAwait(false);

    internal static async Task<bool> TrySendWithStartupRetryAsync(
        FrontendControlRequest request,
        string pipeName,
        TimeSpan startupGracePeriod,
        TimeSpan requestTimeout,
        TimeSpan retryDelay,
        CancellationToken cancellationToken,
        Action<int, bool>? attemptCompleted = null)
        => await FrontendControlPipeClient
            .TrySendWithStartupRetryAsync(
                request,
                pipeName,
                startupGracePeriod,
                requestTimeout,
                retryDelay,
                cancellationToken,
                attemptCompleted)
            .ConfigureAwait(false);
}
