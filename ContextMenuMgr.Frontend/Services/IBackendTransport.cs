using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

internal interface IBackendTransport : IAsyncDisposable
{
    event Action<BackendNotification>? NotificationReceived;

    bool IsConnected { get; }

    void StartNotifications();

    Task<PipeResponse> SendAsync(PipeRequest request, Action<BackendNotification> onNotification, CancellationToken cancellationToken);
}
