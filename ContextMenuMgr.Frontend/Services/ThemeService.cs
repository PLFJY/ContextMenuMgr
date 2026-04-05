using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ContextMenuMgr.Frontend.Services;

public sealed class ThemeService
{
    private readonly FrontendSettingsService _settingsService;

    public ThemeService(FrontendSettingsService settingsService)
    {
        _settingsService = settingsService;
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
}
