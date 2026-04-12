using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class ThemeOptionViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization;

    public ThemeOptionViewModel(AppThemeOption option, LocalizationService localization)
    {
        Option = option;
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
        DisplayName = GetDisplayName();
    }

    public AppThemeOption Option { get; }

    [ObservableProperty]
    public partial string DisplayName { get; private set; }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        DisplayName = GetDisplayName();
    }

    private string GetDisplayName() => Option switch
    {
        AppThemeOption.System => _localization.Translate("ThemeSystem"),
        AppThemeOption.Light => _localization.Translate("ThemeLight"),
        AppThemeOption.Dark => _localization.Translate("ThemeDark"),
        _ => _localization.Translate("ThemeSystem")
    };

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
    }
}
