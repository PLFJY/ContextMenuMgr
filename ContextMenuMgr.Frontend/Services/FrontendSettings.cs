namespace ContextMenuMgr.Frontend.Services;

public sealed class FrontendSettings
{
    public AppLanguageOption Language { get; set; } = AppLanguageOption.System;

    public AppThemeOption Theme { get; set; } = AppThemeOption.System;

    public AppLogLevel LogLevel { get; set; } = AppLogLevel.Warning;

    public bool AutoStartOnLogin { get; set; }

    public bool LaunchMinimized { get; set; }

    public bool LockNewContextMenuItems { get; set; }

    public bool HideDisabledItems { get; set; }

    public bool OpenMoreRegedit { get; set; }

    public bool OpenMoreExplorer { get; set; }
}
