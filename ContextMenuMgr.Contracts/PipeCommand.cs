namespace ContextMenuMgr.Contracts;

public enum PipeCommand
{
    Ping,
    SubscribeNotifications,
    GetSnapshot,
    GetSceneSnapshot,
    SetEnhanceMenuItemEnabled,
    SetEnabled,
    SetShellAttribute,
    SetDisplayText,
    GetRegistryProtectionSetting,
    SetRegistryProtectionSetting,
    ApplyDecision,
    DeleteItem,
    UndoDelete,
    PurgeDeletedItem
}
