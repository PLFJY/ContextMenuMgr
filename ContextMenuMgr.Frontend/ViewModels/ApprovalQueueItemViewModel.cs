using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class ApprovalQueueItemViewModel : ObservableObject
{
    private readonly LocalizationService _localization;

    public ApprovalQueueItemViewModel(
        IReadOnlyList<ContextMenuItemViewModel> sourceItems,
        LocalizationService localization)
    {
        SourceItems = sourceItems;
        _localization = localization;
        Categories = sourceItems
            .Select(static item => item.Category)
            .Distinct()
            .Select(category => ContextMenuCategoryText.GetLocalizedName(category, localization))
            .ToArray();
    }

    public IReadOnlyList<ContextMenuItemViewModel> SourceItems { get; }

    public ContextMenuItemViewModel PrimaryItem => SourceItems[0];

    public IReadOnlyList<string> Categories { get; }

    public string DisplayName => PrimaryItem.DisplayName;

    public string KeyName => PrimaryItem.KeyName;

    public bool ShowKeyName => PrimaryItem.ShowKeyName;

    public string RegistryPath => PrimaryItem.RegistryPath;

    public string Notes => PrimaryItem.Notes;

    public bool ShowNotes => PrimaryItem.ShowNotes;

    public System.Windows.Media.ImageSource? IconSource => PrimaryItem.IconSource;

    public bool CanReviewApproval => SourceItems.Any(static item => item.CanReviewApproval);

    public bool IsWindows11ContextMenu => SourceItems.Any(static item => item.Entry.IsWindows11ContextMenu);

    public string Windows11TagText => _localization.Translate("Windows11PendingApprovalTag");

    public bool HasRegistryBackedItem => SourceItems.Any(static item => item.IsPresentInRegistry && !item.IsDeleted);

    public bool CanRemove => !IsWindows11ContextMenu;

    public string ApprovalRemoveConfirmationText => HasRegistryBackedItem
        ? _localization.Translate("ApprovalRemoveConfirmation")
        : _localization.Translate("ApprovalRemoveWithoutRegistryConfirmation");

    [ObservableProperty]
    public partial bool IsApprovalRemoveFlyoutOpen { get; set; }
}
