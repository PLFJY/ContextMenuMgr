using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

public interface IBackendClient : IAsyncDisposable
{
    event EventHandler<BackendNotification>? NotificationReceived;

    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task PingAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ContextMenuEntry>> GetSnapshotAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ContextMenuEntry>> GetSceneSnapshotAsync(
        ContextMenuSceneKind sceneKind,
        string? scopeValue,
        CancellationToken cancellationToken);

    Task SetEnhanceMenuItemEnabledAsync(
        string groupRegistryPath,
        string itemXml,
        bool enable,
        CancellationToken cancellationToken);

    Task<ContextMenuEntry?> AcknowledgeItemStateAsync(string itemId, CancellationToken cancellationToken);

    Task<ContextMenuEntry?> SetEnabledAsync(string itemId, bool enable, CancellationToken cancellationToken);

    Task<ContextMenuEntry?> SetShellAttributeAsync(
        string itemId,
        ContextMenuShellAttribute attribute,
        bool enable,
        CancellationToken cancellationToken);

    Task<ContextMenuEntry?> SetDisplayTextAsync(
        string itemId,
        string textValue,
        CancellationToken cancellationToken);

    Task<bool> GetRegistryProtectionSettingAsync(CancellationToken cancellationToken);

    Task<bool> SetRegistryProtectionSettingAsync(bool enable, CancellationToken cancellationToken);

    Task<ContextMenuEntry?> ApplyDecisionAsync(string itemId, ContextMenuDecision decision, CancellationToken cancellationToken);

    Task<ContextMenuEntry?> DeleteItemAsync(string itemId, CancellationToken cancellationToken);

    Task<ContextMenuEntry?> UndoDeleteAsync(string itemId, CancellationToken cancellationToken);

    Task PurgeDeletedItemAsync(string itemId, CancellationToken cancellationToken);
}
