namespace ContextMenuMgr.Contracts;

/// <summary>
/// Defines the available pipe Command values.
/// </summary>
public enum PipeCommand
{
    Ping,
    EnsureTrayHost,
    SubscribeNotifications,
    SubscribeTrayHost,
    GetSnapshot,
    GetSceneSnapshot,
    SetEnhanceMenuItemEnabled,
    AcknowledgeItemState,
    SetEnabled,
    SetShellAttribute,
    SetDisplayText,
    GetRegistryProtectionSetting,
    SetRegistryProtectionSetting,
    ApplyDecision,
    DeleteItem,
    UndoDelete,
    PurgeDeletedItem,
    RequestShutdown
}
