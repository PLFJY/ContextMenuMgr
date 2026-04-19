using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the language Option View Model.
/// </summary>
public partial class LanguageOptionViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguageOptionViewModel"/> class.
    /// </summary>
    public LanguageOptionViewModel(AppLanguageOption option, LocalizationService localization)
    {
        Option = option;
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
        DisplayName = GetDisplayName();
    }

    /// <summary>
    /// Gets the option.
    /// </summary>
    public AppLanguageOption Option { get; }

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
        AppLanguageOption.System => _localization.Translate("SystemLanguage"),
        AppLanguageOption.ChineseSimplified => _localization.Translate("ChineseLanguage"),
        AppLanguageOption.EnglishUnitedStates => _localization.Translate("EnglishLanguage"),
        _ => _localization.Translate("SystemLanguage")
    };

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
    }
}
