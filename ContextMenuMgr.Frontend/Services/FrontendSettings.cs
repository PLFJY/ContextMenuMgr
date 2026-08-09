namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the frontend Settings.
/// </summary>
public enum ContextMenuListViewMode
{
    Detailed,
    Compact
}

public sealed class FrontendSettings
{
    public Dictionary<string, string> ContextMenuItemNotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
    /// Gets or sets whether the TrayHost notification-area icon is visible.
    /// </summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether background After Close.
    /// </summary>
    public bool KeepBackgroundAfterClose { get; set; }

    /// <summary>
    /// Gets or sets the lock New Context Menu Items.
    /// </summary>
    public bool LockNewContextMenuItems { get; set; }

    /// <summary>
    /// Gets or sets whether the Windows 11 modern context menu is disabled.
    /// </summary>
    public bool Win11ModernContextMenuDisabled { get; set; }

    /// <summary>
    /// Gets or sets the hide Disabled Items.
    /// </summary>
    public bool HideDisabledItems { get; set; }

    /// <summary>
    /// Gets or sets the shared context-menu list presentation mode.
    /// </summary>
    public ContextMenuListViewMode ContextMenuListViewMode { get; set; } = ContextMenuListViewMode.Compact;

    /// <summary>
    /// Gets or sets the open More Regedit.
    /// </summary>
    public bool OpenMoreRegedit { get; set; }

    /// <summary>
    /// Gets or sets the open More Explorer.
    /// </summary>
    public bool OpenMoreExplorer { get; set; }

    /// <summary>
    /// Gets or sets the main window restore bounds left coordinate.
    /// </summary>
    public double? MainWindowLeft { get; set; }

    /// <summary>
    /// Gets or sets the main window restore bounds top coordinate.
    /// </summary>
    public double? MainWindowTop { get; set; }

    /// <summary>
    /// Gets or sets the main window restore bounds width.
    /// </summary>
    public double? MainWindowWidth { get; set; }

    /// <summary>
    /// Gets or sets the main window restore bounds height.
    /// </summary>
    public double? MainWindowHeight { get; set; }

    /// <summary>
    /// Gets or sets the main window state to restore.
    /// </summary>
    public string? MainWindowState { get; set; }
}
