using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class DetailedEditGroupViewModel : ObservableObject
{
    public DetailedEditGroupViewModel(
        DetailedEditGroupDefinition definition,
        DetailedEditRuleService ruleService,
        LocalizationService localization)
    {
        Title = definition.Title;
        RegistryPath = definition.RegistryPath;
        FilePath = definition.FilePath;
        IsIniGroup = definition.IsIniGroup;
        IsAvailable = definition.IsAvailable;
        Rules = definition.Rules
            .Select(rule => new DetailedEditRuleViewModel(rule, ruleService, localization))
            .ToArray();
    }

    public string Title { get; }

    public string? RegistryPath { get; }

    public string? FilePath { get; }

    public bool IsIniGroup { get; }

    public bool IsAvailable { get; }

    public IReadOnlyList<DetailedEditRuleViewModel> Rules { get; }
}
