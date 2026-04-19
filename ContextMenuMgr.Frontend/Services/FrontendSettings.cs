namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the frontend Settings.
/// </summary>
public sealed class FrontendSettings
{
    /// <summary>
    /// Gets or sets the language.
    /// </summary>
    public AppLanguageOption Language { get; set; } = AppLanguageOption.System;

    /// <summary>
    /// Gets or sets the theme.
    /// </summary>
    public AppThemeOption Theme { get; set; } = AppThemeOption.System;

    /// <summary>
    /// Gets or sets the log Level.
    /// </summary>
    public AppLogLevel LogLevel { get; set; } = AppLogLevel.Warning;

    /// <summary>
    /// Gets or sets the auto Start On Login.
    /// </summary>
    public bool AutoStartOnLogin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether background After Close.
    /// </summary>
    public bool KeepBackgroundAfterClose { get; set; }

    /// <summary>
    /// Gets or sets the lock New Context Menu Items.
    /// </summary>
    public bool LockNewContextMenuItems { get; set; }

    /// <summary>
    /// Gets or sets the hide Disabled Items.
    /// </summary>
    public bool HideDisabledItems { get; set; }

    /// <summary>
    /// Gets or sets the open More Regedit.
    /// </summary>
    public bool OpenMoreRegedit { get; set; }

    /// <summary>
    /// Gets or sets the open More Explorer.
    /// </summary>
    public bool OpenMoreExplorer { get; set; }
}
