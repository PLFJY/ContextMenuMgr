#if CONTEXTMENUMGR_IN_PROCESS
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

internal enum InProcessBackendRoute { Unavailable, Host, Shared }

internal static class InProcessBackendCapabilities
{
    public static InProcessBackendRoute Route(PipeCommand command) => command switch
    {
        PipeCommand.Ping or PipeCommand.SubscribeNotifications or PipeCommand.SetLogLevel
            or PipeCommand.RestartExplorer or PipeCommand.RepairRuntimeDataAcl => InProcessBackendRoute.Host,

        PipeCommand.GetSnapshot or PipeCommand.GetSceneSnapshot or PipeCommand.SetEnhanceMenuItemEnabled
            or PipeCommand.SetDetailedEditRuleValue or PipeCommand.AcknowledgeItemState or PipeCommand.SetEnabled
            or PipeCommand.SetShellAttribute or PipeCommand.SetDisplayText or PipeCommand.SetCommandText
            or PipeCommand.GetShellSubMenuItems or PipeCommand.SetShellSubMenuItemEnabled
            or PipeCommand.DeleteItem or PipeCommand.UndoDelete or PipeCommand.PurgeDeletedItem
            or PipeCommand.ResetStateDatabase or PipeCommand.GetSpecialMenuSnapshot or PipeCommand.SetSpecialMenuItemEnabled
            or PipeCommand.CreateSpecialMenuItem or PipeCommand.UpdateSpecialMenuItem or PipeCommand.DeleteSpecialMenuItem
            or PipeCommand.UndoSpecialMenuItem or PipeCommand.PurgeSpecialMenuItem or PipeCommand.MoveSpecialMenuItem
            or PipeCommand.RestoreSpecialMenuDefaults or PipeCommand.SetShellNewOrderLock or PipeCommand.SetWin11BlockedItem
            or PipeCommand.RemoveWin11BlockedItem or PipeCommand.GetWin11BlockedItems or PipeCommand.GetWin11ContextMenuSnapshot
            or PipeCommand.GetWin11ModernContextMenuDisabled or PipeCommand.SetWin11ModernContextMenuDisabled
            or PipeCommand.AnalyzeFileTypeContext or PipeCommand.CreateSceneMenuItem or PipeCommand.FindRelatedFileTypeMenuItems
            or PipeCommand.GetOfficeSuiteCoexistenceStatus
            or PipeCommand.GetContextMenuItemState or PipeCommand.InspectOpenWithSources or PipeCommand.GetShellNewOrderLock
            or PipeCommand.SetWin11ApplicationEnabled or PipeCommand.GetSpecialMenuItemState or PipeCommand.DeleteFileTypeBatchItem
            or PipeCommand.GetFileTypeBatchItemState or PipeCommand.PurgeDeletedItems or PipeCommand.SetDocumentIconRepair
            => InProcessBackendRoute.Shared,

        // Service lifecycle, background policy and obsolete commands.
        _ => InProcessBackendRoute.Unavailable
    };
}
#endif
