using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the detailed Edit Rule View Model.
/// </summary>
public partial class DetailedEditRuleViewModel : ObservableObject
{
    private readonly DetailedEditRuleDefinition _definition;
    private readonly DetailedEditRuleService _ruleService;
    private readonly LocalizationService _localization;
    private bool _suppressAutoApply;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetailedEditRuleViewModel"/> class.
    /// </summary>
    public DetailedEditRuleViewModel(
        DetailedEditRuleDefinition definition,
        DetailedEditRuleService ruleService,
        LocalizationService localization)
    {
        _definition = definition;
        _ruleService = ruleService;
        _localization = localization;

        DisplayName = definition.DisplayName;
        Tip = definition.Tip;
        RequiresExplorerRestart = definition.RestartExplorer;
        EditorKind = definition.EditorKind;

        Refresh();
    }

    /// <summary>
    /// Gets the display Name.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the tip.
    /// </summary>
    public string? Tip { get; }

    /// <summary>
    /// Gets the requires Explorer Restart.
    /// </summary>
    public bool RequiresExplorerRestart { get; }

    /// <summary>
    /// Gets the editor Kind.
    /// </summary>
    public RuleValueEditorKind EditorKind { get; }

    public bool IsBooleanRule => EditorKind == RuleValueEditorKind.Boolean;

    public bool IsNumberRule => EditorKind == RuleValueEditorKind.Number;

    public bool IsStringRule => EditorKind == RuleValueEditorKind.String;

    public string RestartExplorerHint => _localization.Translate("RestartExplorerHint");

    public string ApplyText => _localization.Translate("Apply");

    public string ToggleLabel => _localization.Translate(BoolValue ? "ToggleOn" : "ToggleOff");

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public bool CanToggle => !IsBusy;

    /// <summary>
    /// Gets or sets the bool Value.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleLabel))]
    public partial bool BoolValue { get; set; }

    /// <summary>
    /// Gets or sets the number Text.
    /// </summary>
    [ObservableProperty]
    public partial string NumberText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the string Value.
    /// </summary>
    [ObservableProperty]
    public partial string StringValue { get; set; } = string.Empty;

    partial void OnBoolValueChanged(bool oldValue, bool newValue)
    {
        if (_suppressAutoApply)
        {
            return;
        }

        if (IsBusy)
        {
            _suppressAutoApply = true;
            BoolValue = oldValue;
            _suppressAutoApply = false;
            return;
        }

        IsBusy = true;
        _ = ApplyBooleanAsync(oldValue, newValue);
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanToggle));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private bool CanApply() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        IsBusy = true;
        try
        {
            switch (EditorKind)
            {
                case RuleValueEditorKind.Number:
                    if (!int.TryParse(NumberText, out var number))
                    {
                        throw new InvalidOperationException(_localization.Translate("NumberRuleInvalid"));
                    }

                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        await _ruleService.WriteNumberAsync(_definition, number, cts.Token);
                    }

                    NumberText = _ruleService.ReadNumber(_definition).ToString();
                    break;

                case RuleValueEditorKind.String:
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        await _ruleService.WriteStringAsync(_definition, StringValue, cts.Token);
                    }

                    StringValue = _ruleService.ReadString(_definition);
                    break;
            }

            await ShowRestartHintIfNeededAsync();
        }
        catch (Exception ex)
        {
            if (RegistryProtectionDialog.IsRegistryProtectionError(ex))
            {
                await RegistryProtectionDialog.ShowAsync(_localization);
                Refresh();
                return;
            }

            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                DisplayName);
            Refresh();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Executes refresh.
    /// </summary>
    public void Refresh()
    {
        _suppressAutoApply = true;
        try
        {
            switch (EditorKind)
            {
                case RuleValueEditorKind.Boolean:
                    BoolValue = _ruleService.ReadBoolean(_definition);
                    break;
                case RuleValueEditorKind.Number:
                    NumberText = _ruleService.ReadNumber(_definition).ToString();
                    break;
                case RuleValueEditorKind.String:
                    StringValue = _ruleService.ReadString(_definition);
                    break;
            }
        }
        finally
        {
            _suppressAutoApply = false;
        }
    }

    private async Task ApplyBooleanAsync(bool oldValue, bool value)
    {
        try
        {
            await ToggleBusyPresentation.WaitForFirstFrameAsync();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await _ruleService.WriteBooleanAsync(_definition, value, cts.Token);
            }

            await ShowRestartHintIfNeededAsync();
        }
        catch (Exception ex)
        {
            _suppressAutoApply = true;
            try
            {
                BoolValue = _ruleService.ReadBoolean(_definition);
            }
            catch
            {
                BoolValue = oldValue;
            }
            finally
            {
                _suppressAutoApply = false;
            }
            if (RegistryProtectionDialog.IsRegistryProtectionError(ex))
            {
                await RegistryProtectionDialog.ShowAsync(_localization);
                return;
            }

            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                DisplayName);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task ShowRestartHintIfNeededAsync()
    {
        return RequiresExplorerRestart
            ? FrontendMessageBox.ShowInfoAsync(RestartExplorerHint, DisplayName)
            : Task.CompletedTask;
    }
}
