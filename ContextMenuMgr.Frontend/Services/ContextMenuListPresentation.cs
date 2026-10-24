using System.ComponentModel;
using System.Windows.Data;
using ContextMenuMgr.Frontend.ViewModels;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Holds the shared, frontend-only presentation rules for classic and scene
/// context-menu entry lists.
/// </summary>
public static class ContextMenuListPresentation
{
    public static IReadOnlyList<string> SortPropertyNames { get; } =
    [
        nameof(ContextMenuItemViewModel.SortAttentionWeight),
        nameof(ContextMenuItemViewModel.SortDeletedWeight),
        nameof(ContextMenuItemViewModel.DisplayName)
    ];

    public static void ConfigureSort(ListCollectionView itemsView)
    {
        foreach (var propertyName in SortPropertyNames)
        {
            itemsView.SortDescriptions.Add(new SortDescription(propertyName, ListSortDirection.Ascending));
        }
        itemsView.IsLiveSorting = true;
        itemsView.IsLiveFiltering = true;
        foreach (var property in new[] { nameof(ContextMenuItemViewModel.IsEnabled), nameof(ContextMenuItemViewModel.IsDeleted),
            nameof(ContextMenuItemViewModel.DisplayName), nameof(ContextMenuItemViewModel.IsWindows11ContextMenu),
            nameof(ContextMenuItemViewModel.KeyName), nameof(ContextMenuItemViewModel.RegistryPath), nameof(ContextMenuItemViewModel.Notes),
            nameof(ContextMenuItemViewModel.UserNote), nameof(ContextMenuItemViewModel.Subtitle), nameof(ContextMenuItemViewModel.IsToggleBusy), nameof(ContextMenuItemViewModel.IsToggleOutcomeUncertain) })
            itemsView.LiveFilteringProperties.Add(property);
    }

    /// <summary>
    /// Deleted entries deliberately remain visible while disabled non-deleted
    /// entries are hidden, so recovery remains available.
    /// </summary>
    public static bool IsVisibleWithDisabledFilter(bool isEnabled, bool isDeleted, bool hideDisabledItems, bool isOperationPending = false)
        => !hideDisabledItems || isEnabled || isDeleted || isOperationPending;
}
