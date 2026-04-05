using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class LanguageOptionViewModel : ObservableObject
{
    private readonly LocalizationService _localization;

    public LanguageOptionViewModel(AppLanguageOption option, LocalizationService localization)
    {
        Option = option;
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
        DisplayName = GetDisplayName();
    }

    public AppLanguageOption Option { get; }

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
}
