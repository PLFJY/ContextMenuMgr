using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the log Level Option View Model.
/// </summary>
public partial class LogLevelOptionViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="LogLevelOptionViewModel"/> class.
    /// </summary>
    public LogLevelOptionViewModel(AppLogLevel option, LocalizationService localization)
    {
        Option = option;
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
        DisplayName = GetDisplayName();
    }

    /// <summary>
    /// Gets the option.
    /// </summary>
    public AppLogLevel Option { get; }

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
        AppLogLevel.Information => _localization.Translate("LogLevelInformation"),
        AppLogLevel.Warning => _localization.Translate("LogLevelWarning"),
        AppLogLevel.Error => _localization.Translate("LogLevelError"),
        _ => _localization.Translate("LogLevelWarning")
    };

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
    }
}
