namespace ContextMenuMgr.Contracts;

/// <summary>Request evidence policy shared by both ends of the pipe; not mutation routing.</summary>
public static class PipeOperationLogging
{
    // Snapshot/WPS queries can perform existing baseline/reconciliation work internally.
    // Only their routine successful request records are quiet; internal errors remain independent.
    public static bool IsRoutineQuery(PipeCommand command) => command is
        PipeCommand.Ping or PipeCommand.SubscribeNotifications or PipeCommand.SubscribeTrayHost or
        PipeCommand.GetSnapshot or PipeCommand.GetSceneSnapshot or PipeCommand.GetSpecialMenuSnapshot or
        PipeCommand.GetShellSubMenuItems or PipeCommand.GetContextMenuItemState or
        PipeCommand.GetRegistryProtectionSetting or PipeCommand.GetWin11BlockedItems or
        PipeCommand.GetWin11ContextMenuSnapshot or PipeCommand.GetWin11ModernContextMenuDisabled or
        PipeCommand.GetAutoStartEnabled or PipeCommand.AnalyzeFileTypeContext or
        PipeCommand.FindRelatedFileTypeMenuItems or PipeCommand.GetEnhanceMenuDefinitions or
        PipeCommand.GetEnhanceMenuState or PipeCommand.GetOfficeSuiteCoexistenceStatus or
        PipeCommand.GetWpsOfficePendingApprovals or PipeCommand.InspectOpenWithSources or PipeCommand.GetShellNewOrderLock;

    public static bool ShouldWrite(PipeCommand command, RuntimeLogLevel configuredLevel, bool failed = false)
        => failed || !IsRoutineQuery(command) || configuredLevel == RuntimeLogLevel.Information;
}
