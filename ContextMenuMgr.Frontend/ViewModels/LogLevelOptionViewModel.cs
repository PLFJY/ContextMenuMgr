using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class LogLevelOptionViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization;

    public LogLevelOptionViewModel(AppLogLevel option, LocalizationService localization)
    {
        Option = option;
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
        DisplayName = GetDisplayName();
    }

    public AppLogLevel Option { get; }

    [ObservableProperty]
    public partial string DisplayName { get; private set; }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        DisplayName = GetDisplayName();
    }

    private string GetDisplayName() => Option switch
    {
        AppLogLevel.Information => _localization.Translate("LogLevelInformation"),
        AppLogLevel.Warning => _localization.Translate("LogLevelWarning"),
        AppLogLevel.Error => _localization.Translate("LogLevelError"),
        _ => _localization.Translate("LogLevelWarning")
    };

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
    }
}
