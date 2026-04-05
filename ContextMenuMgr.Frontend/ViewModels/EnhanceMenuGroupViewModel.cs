using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class EnhanceMenuGroupViewModel : ObservableObject
{
    public EnhanceMenuGroupViewModel(
        EnhanceMenuGroupDefinition definition,
        LocalizationService localization,
        EnhanceMenuRuleService ruleService,
        IconPreviewService iconPreviewService,
        Func<Task>? refreshAsync = null)
    {
        Title = definition.Title;
        RegistryPath = definition.RegistryPath;
        IconPath = definition.IconPath;
        Items = definition.Items
            .Select(item => new EnhanceMenuItemViewModel(item, IconPath, localization, ruleService, iconPreviewService, refreshAsync))
            .ToArray();
    }

    public string Title { get; }

    public string RegistryPath { get; }

    public string? IconPath { get; }

    public IReadOnlyList<EnhanceMenuItemViewModel> Items { get; }

    public void RefreshStates()
    {
        foreach (var item in Items)
        {
            item.RefreshState();
        }
    }
}
