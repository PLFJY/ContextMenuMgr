#if CONTEXTMENUMGR_IN_PROCESS
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

internal sealed class InProcessBackendTransport(
    Func<PipeRequest, InProcessBackendRoute, CancellationToken, Task<BackendDispatchResult>> dispatch,
    Func<string?> userSid,
    FileLogger logger) : IBackendTransport
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _notificationsStarted;
    private bool _stopping;
    private int _active;

    public event Action<BackendNotification>? NotificationReceived;

    public bool IsConnected
    {
        get { lock (_gate) return _notificationsStarted && !_stopping; }
    }

    public void StartNotifications()
    {
        lock (_gate)
        {
            if (!_stopping) _notificationsStarted = true;
        }
    }

    public Task<PipeResponse> SendAsync(PipeRequest request, Action<BackendNotification> onNotification,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var route = InProcessBackendCapabilities.Route(request.Command);
        if (route == InProcessBackendRoute.Unavailable)
        {
            return Task.FromResult(BackendRequestDispatcher.CorrelateResponseWithRequest(request, new PipeResponse
            {
                ErrorCode = PipeErrorCodes.BackendCapabilityUnavailable,
                Message = $"{request.Command} is unavailable in In-process Portable."
            }));
        }

        var snapshot = InProcessBackendSnapshots.Request(request);
        lock (_gate)
        {
            if (_stopping) throw new OperationCanceledException("The local backend is shutting down.");
            _active++;
        }

        var execution = ExecuteAsync(snapshot, route, onNotification);
        // Some discovery operations also establish baselines or repair/replace stored state.
        // A caller's deadline releases its wait while admitted work reaches a safe terminal state.
        return execution.WaitAsync(cancellationToken);
    }

    public void PublishNotification(BackendNotification notification)
    {
        if (!IsConnected) return;
        PublishSafely(NotificationReceived, InProcessBackendSnapshots.Notification(notification));
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            _notificationsStarted = false;
            if (_active == 0) _drained.TrySetResult();
        }
        return new ValueTask(_drained.Task);
    }

    private async Task<PipeResponse> ExecuteAsync(PipeRequest request, InProcessBackendRoute route,
        Action<BackendNotification> onNotification)
    {
        try
        {
            // Give the caller its cancellable wait before synchronous registry enumeration starts.
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            BackendDispatchResult result;
            try
            {
                result = await dispatch(request, route, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = BackendRequestDispatcher.CreateFailure(request, ex);
            }

            var response = BackendRequestDispatcher.CorrelateResponseWithRequest(request, result.Response);
            if (result.Error is { } error)
                logger.LogFireAndForget(RuntimeLogLevel.Warning, $"In-process backend command {request.Command} failed.", error);

            await BackendRequestDispatcher.PublishMutationNotificationsAsync(request, response, notification =>
            {
                if (notification.SpecialKind == SpecialMenuKind.OpenWith || notification.Kind == PipeNotificationKind.DeletedItemsPurged)
                    notification = notification with { UserSid = userSid() };
                PublishNotification(notification);
                PublishSafely(onNotification, InProcessBackendSnapshots.Notification(notification));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            return InProcessBackendSnapshots.Response(response);
        }
        finally
        {
            lock (_gate)
            {
                if (--_active == 0 && _stopping) _drained.TrySetResult();
            }
        }
    }

    private void PublishSafely(Action<BackendNotification>? observers, BackendNotification notification)
    {
        if (observers is null) return;
        foreach (Action<BackendNotification> observer in observers.GetInvocationList())
        {
            try { observer(notification); }
            catch (Exception ex)
            {
                logger.LogFireAndForget(RuntimeLogLevel.Warning, "In-process backend notification subscriber failed.", ex);
            }
        }
    }
}
#endif
