using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Services;
using System.Windows.Media;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the enhance Menu Item View Model.
/// </summary>
public partial class EnhanceMenuItemViewModel : ObservableObject, IDisposable
{
    private readonly EnhanceMenuRuleService _ruleService;
    private readonly IconPreviewService _iconPreviewService;
    private readonly LocalizationService _localization;
    private readonly Func<Task>? _refreshAsync;
    private readonly EventHandler _languageChangedHandler;
    private bool _suppressSync;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnhanceMenuItemViewModel"/> class.
    /// </summary>
    public EnhanceMenuItemViewModel(
        EnhanceMenuItemDefinition definition,
        string? groupIconPath,
        LocalizationService localization,
        EnhanceMenuRuleService ruleService,
        IconPreviewService iconPreviewService,
        Func<Task>? refreshAsync = null)
    {
        Definition = definition;
        GroupIconPath = groupIconPath;
        _ruleService = ruleService;
        _iconPreviewService = iconPreviewService;
        _localization = localization;
        _refreshAsync = refreshAsync;
        IsEnabled = _ruleService.IsEnabled(definition);
        _languageChangedHandler = (_, _) =>
        {
            OnPropertyChanged(nameof(ToggleOnText));
            OnPropertyChanged(nameof(ToggleOffText));
        };
        localization.LanguageChanged += _languageChangedHandler;
    }

    /// <summary>
    /// Gets the definition.
    /// </summary>
    public EnhanceMenuItemDefinition Definition { get; }

    /// <summary>
    /// Gets the group Icon Path.
    /// </summary>
    public string? GroupIconPath { get; }

    public string DisplayName => Definition.DisplayName;

    public string Kind => Definition.Kind;

    public string KeyName => Definition.KeyName;

    public string? Tip => Definition.Tip;

    public bool HasTip => !string.IsNullOrWhiteSpace(Definition.Tip);

    public ImageSource? IconSource => _iconPreviewService.GetIcon(Definition.IconPath ?? GroupIconPath, 0, null);

    public bool HasIcon => IconSource is not null;

    public string ToggleOnText => _localization.Translate("ToggleOn");

    public string ToggleOffText => _localization.Translate("ToggleOff");

    /// <summary>
    /// Gets or sets a value indicating whether enabled.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether busy.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsBusy { get; set; }

    public bool CanToggle => !IsBusy;

    /// <summary>
    /// Refreshes state.
    /// </summary>
    public void RefreshState()
    {
        var current = _ruleService.IsEnabled(Definition);
        _suppressSync = true;
        try
        {
            IsEnabled = current;
        }
        finally
        {
            _suppressSync = false;
        }
    }

    partial void OnIsEnabledChanged(bool oldValue, bool newValue)
    {
        if (_suppressSync || oldValue == newValue)
        {
            return;
        }

        IsBusy = true;
        _ = SyncAsync(oldValue, newValue);
    }

    private async Task SyncAsync(bool oldValue, bool newValue)
    {
        try
        {
            await _ruleService.SetEnabledAsync(Definition, newValue, CancellationToken.None);
            if (_refreshAsync is not null)
            {
                await _refreshAsync();
            }
            else
            {
                RefreshState();
            }
        }
        catch
        {
            _suppressSync = true;
            try
            {
                IsEnabled = oldValue;
            }
            finally
            {
                _suppressSync = false;
            }
            await FrontendMessageBox.ShowErrorAsync(
                _localization.Translate("EnhanceMenuToggleFailed"),
                DisplayName);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        _localization.LanguageChanged -= _languageChangedHandler;
    }
}
