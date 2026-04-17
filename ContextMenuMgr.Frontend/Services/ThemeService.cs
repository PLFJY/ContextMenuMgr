using Microsoft.Win32;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ContextMenuMgr.Frontend.Services;

public sealed class ThemeService : IDisposable
{
    private readonly FrontendSettingsService _settingsService;
    private bool _disposed;

    public ThemeService(FrontendSettingsService settingsService)
    {
        _settingsService = settingsService;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public AppThemeOption CurrentTheme => _settingsService.Current.Theme;

    public void ApplyPersistedTheme()
    {
        ApplyTheme(_settingsService.Current.Theme, persist: false);
    }

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

        // 系统主题变化通常会落在这几个类别里，保守一点一起处理
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