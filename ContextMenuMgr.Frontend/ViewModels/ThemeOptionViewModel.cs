using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the theme Option View Model.
/// </summary>
public partial class ThemeOptionViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThemeOptionViewModel"/> class.
    /// </summary>
    public ThemeOptionViewModel(AppThemeOption option, LocalizationService localization)
    {
        Option = option;
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
        DisplayName = GetDisplayName();
    }

    /// <summary>
    /// Gets the option.
    /// </summary>
    public AppThemeOption Option { get; }

    /// <summary>
    /// Gets or sets the display Name.
    /// </summary>
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

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
    }
}
