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

    /// <summary>
    /// Gets or sets the bool Value.
    /// </summary>
    [ObservableProperty]
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

    partial void OnBoolValueChanged(bool value)
    {
        if (_suppressAutoApply)
        {
            return;
        }

        _ = ApplyBooleanAsync(value);
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        try
        {
            switch (EditorKind)
            {
                case RuleValueEditorKind.Number:
                    if (!int.TryParse(NumberText, out var number))
                    {
                        throw new InvalidOperationException(_localization.Translate("NumberRuleInvalid"));
                    }

                    _ruleService.WriteNumber(_definition, number);
                    NumberText = _ruleService.ReadNumber(_definition).ToString();
                    break;

                case RuleValueEditorKind.String:
                    _ruleService.WriteString(_definition, StringValue);
                    StringValue = _ruleService.ReadString(_definition);
                    break;
            }

            await ShowRestartHintIfNeededAsync();
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                DisplayName);
            Refresh();
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

    private async Task ApplyBooleanAsync(bool value)
    {
        try
        {
            _ruleService.WriteBoolean(_definition, value);
            await ShowRestartHintIfNeededAsync();
        }
        catch (Exception ex)
        {
            _suppressAutoApply = true;
            BoolValue = _ruleService.ReadBoolean(_definition);
            _suppressAutoApply = false;
            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                DisplayName);
        }
    }

    private Task ShowRestartHintIfNeededAsync()
    {
        return RequiresExplorerRestart
            ? FrontendMessageBox.ShowInfoAsync(RestartExplorerHint, DisplayName)
            : Task.CompletedTask;
    }
}
