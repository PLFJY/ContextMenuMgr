using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Defines the contract for i Backend Client.
/// </summary>
public interface IBackendClient : IAsyncDisposable
{
    event EventHandler<BackendNotification>? NotificationReceived;

    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task PingAsync(CancellationToken cancellationToken);

    Task EnsureTrayHostAsync(CancellationToken cancellationToken);

    Task RequestShutdownAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ContextMenuEntry>> GetSnapshotAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ContextMenuEntry>> GetSceneSnapshotAsync(
        ContextMenuSceneKind sceneKind,
        string? scopeValue,
        CancellationToken cancellationToken);

    Task SetEnhanceMenuItemEnabledAsync(
        string groupRegistryPath,
        string itemXml,
        bool enable,
        string cultureName,
        CancellationToken cancellationToken);

    Task SetDetailedEditRuleValueAsync(
        string storageKind,
        string path,
        string? section,
        string keyName,
        string valueKind,
        string? value,
        string? userSid,
        CancellationToken cancellationToken);

    Task<ContextMenuEntry?> AcknowledgeItemStateAsync(string itemId, CancellationToken cancellationToken);

    Task<ContextMenuEntry?> SetEnabledAsync(
        string itemId,
        bool enable,
        CancellationToken cancellationToken,
        ContextMenuEntry? item = null);

    Task<ContextMenuEntry?> SetShellAttributeAsync(
        string itemId,
        ContextMenuShellAttribute attribute,
        bool enable,
        CancellationToken cancellationToken);

    Task<ContextMenuEntry?> SetDisplayTextAsync(
        string itemId,
        string textValue,
        CancellationToken cancellationToken);

    Task<ContextMenuEntry?> SetCommandTextAsync(
        string itemId,
        string commandText,
        CancellationToken cancellationToken);

    Task<bool> GetRegistryProtectionSettingAsync(CancellationToken cancellationToken);

    Task<bool> SetRegistryProtectionSettingAsync(bool enable, CancellationToken cancellationToken);

    Task<ContextMenuEntry?> ApplyDecisionAsync(string itemId, ContextMenuDecision decision, CancellationToken cancellationToken);

    Task<ContextMenuEntry?> DeleteItemAsync(string itemId, CancellationToken cancellationToken, ContextMenuEntry? item = null);

    Task<ContextMenuEntry?> UndoDeleteAsync(string itemId, CancellationToken cancellationToken);

    Task PurgeDeletedItemAsync(string itemId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SpecialMenuEntry>> GetSpecialMenuSnapshotAsync(
        SpecialMenuKind kind,
        CancellationToken cancellationToken);

    Task<SpecialMenuEntry?> SetSpecialMenuItemEnabledAsync(
        SpecialMenuEntry item,
        bool enable,
        Guid clientOperationId,
        CancellationToken cancellationToken);

    Task<SpecialMenuEntry?> CreateSpecialMenuItemAsync(PipeRequest request, CancellationToken cancellationToken);

    Task<SpecialMenuEntry?> UpdateSpecialMenuItemAsync(PipeRequest request, CancellationToken cancellationToken);

    Task<SpecialMenuEntry?> DeleteSpecialMenuItemAsync(SpecialMenuEntry item, Guid clientOperationId, CancellationToken cancellationToken);

    Task<SpecialMenuEntry?> UndoDeleteSpecialMenuItemAsync(SpecialMenuEntry item, Guid clientOperationId, CancellationToken cancellationToken);

    Task PurgeDeletedSpecialMenuItemAsync(SpecialMenuEntry item, Guid clientOperationId, CancellationToken cancellationToken);

    Task<SpecialMenuEntry?> MoveSpecialMenuItemAsync(PipeRequest request, CancellationToken cancellationToken);

    Task RestoreSpecialMenuDefaultsAsync(
        SpecialMenuKind kind,
        string? scopeValue,
        Guid clientOperationId,
        CancellationToken cancellationToken);

    Task SetShellNewOrderLockAsync(bool locked, Guid clientOperationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FileTypeAnalysisResult>> AnalyzeFileTypeContextAsync(string path, CancellationToken cancellationToken);

    Task<ContextMenuEntry?> CreateSceneMenuItemAsync(
        CreateSceneMenuItemRequest request,
        Guid clientOperationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ContextMenuEntry>> FindRelatedFileTypeMenuItemsAsync(
        FileTypeBatchQuery query,
        CancellationToken cancellationToken);

    Task RestartExplorerAsync(CancellationToken cancellationToken);

    Task RepairRuntimeDataAclAsync(CancellationToken cancellationToken);

    Task SetWin11BlockedItemAsync(
        string handlerClsid,
        string displayName,
        bool blockMachine,
        Guid clientOperationId,
        CancellationToken cancellationToken);

    Task RemoveWin11BlockedItemAsync(
        string handlerClsid,
        bool unblockMachine,
        Guid clientOperationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Win11BlockedItem>> GetWin11BlockedItemsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ContextMenuEntry>> GetWin11ContextMenuSnapshotAsync(CancellationToken cancellationToken);

    Task<bool> GetWin11ModernContextMenuDisabledAsync(CancellationToken cancellationToken);

    Task SetWin11ModernContextMenuDisabledAsync(bool disabled, CancellationToken cancellationToken);

    Task SetAutoStartEnabledAsync(bool enabled, CancellationToken cancellationToken, bool? showTrayIcon = null);

    Task<bool> GetAutoStartEnabledAsync(CancellationToken cancellationToken);

    Task SetTrayIconPolicyAsync(bool showTrayIcon, CancellationToken cancellationToken);

    Task SetLogLevelAsync(RuntimeLogLevel logLevel, CancellationToken cancellationToken);

    Task<OfficeSuiteCoexistenceStatus?> GetOfficeSuiteCoexistenceStatusAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ContextMenuEntry>> GetWpsOfficePendingApprovalsAsync(CancellationToken cancellationToken);

    Task<OfficeSuiteCoexistenceStatus?> SetDocumentIconProviderAsync(
        DocumentIconProvider provider,
        CancellationToken cancellationToken);

    Task<ShellProxyWrapperStatus?> CreateShellProxyWrapperAsync(ContextMenuEntry item, string title, CancellationToken cancellationToken);
    Task RemoveShellProxyWrapperAsync(ContextMenuEntry item, CancellationToken cancellationToken);
    Task<ShellProxyWrapperStatus?> UpdateShellProxyWrapperAsync(ContextMenuEntry item, string title, CancellationToken cancellationToken);
}
