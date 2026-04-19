using Microsoft.Win32;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the theme Service.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private readonly FrontendSettingsService _settingsService;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThemeService"/> class.
    /// </summary>
    public ThemeService(FrontendSettingsService settingsService)
    {
        _settingsService = settingsService;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public AppThemeOption CurrentTheme => _settingsService.Current.Theme;

    /// <summary>
    /// Applies persisted Theme.
    /// </summary>
    public void ApplyPersistedTheme()
    {
        ApplyTheme(_settingsService.Current.Theme, persist: false);
    }

    /// <summary>
    /// Applies theme.
    /// </summary>
    public void ApplyTheme(AppThemeOption option, bool persist = true)
    {
        switch (option)
        {
            case AppThemeOption.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica);
                break;
            case AppThemeOption.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                break;
        }

        if (persist)
        {
            _settingsService.UpdateTheme(option);
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_settingsService.Current.Theme != AppThemeOption.System)
        {
            return;
        }

        // 绯荤粺涓婚鍙樺寲閫氬父浼氳惤鍦ㄨ繖鍑犱釜绫诲埆閲岋紝淇濆畧涓€鐐逛竴璧峰鐞?
        if (e.Category is not UserPreferenceCategory.General
            and not UserPreferenceCategory.Color
            and not UserPreferenceCategory.VisualStyle
            and not UserPreferenceCategory.Window)
        {
            return;
        }

        if (Application.Current is null)
        {
            return;
        }

        _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_settingsService.Current.Theme == AppThemeOption.System)
            {
                ApplicationThemeManager.ApplySystemTheme();
            }
        });
    }

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}
