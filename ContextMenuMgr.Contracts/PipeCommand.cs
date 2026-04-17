namespace ContextMenuMgr.Contracts;

public enum PipeCommand
{
    Ping,
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
