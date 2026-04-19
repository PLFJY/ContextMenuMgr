using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the enhance Menu Group View Model.
/// </summary>
public partial class EnhanceMenuGroupViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EnhanceMenuGroupViewModel"/> class.
    /// </summary>
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

    /// <summary>
    /// Gets the title.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the registry Path.
    /// </summary>
    public string RegistryPath { get; }

    /// <summary>
    /// Gets the icon Path.
    /// </summary>
    public string? IconPath { get; }

    /// <summary>
    /// Gets the items.
    /// </summary>
    public IReadOnlyList<EnhanceMenuItemViewModel> Items { get; }

    /// <summary>
    /// Refreshes states.
    /// </summary>
    public void RefreshStates()
    {
        foreach (var item in Items)
        {
            item.RefreshState();
        }
    }

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        foreach (var item in Items)
        {
            item.Dispose();
        }
    }
}
