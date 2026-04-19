using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the approval Queue Item View Model.
/// </summary>
public partial class ApprovalQueueItemViewModel : ObservableObject
{
    private readonly LocalizationService _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApprovalQueueItemViewModel"/> class.
    /// </summary>
    public ApprovalQueueItemViewModel(
        IReadOnlyList<ContextMenuItemViewModel> sourceItems,
        LocalizationService localization)
    {
        SourceItems = sourceItems;
        _localization = localization;
        var categoryTags = sourceItems
            .Select(static item => item.Category)
            .Distinct()
            .Select(category => ContextMenuCategoryText.GetLocalizedName(category, localization))
            .Select(static category => new ApprovalTagChipViewModel(category, isWindows11Tag: false))
            .ToList();

        if (IsWindows11ContextMenu)
        {
            categoryTags.Insert(0, new ApprovalTagChipViewModel(localization.Translate("Windows11PendingApprovalTag"), isWindows11Tag: true));
        }

        CategoryTags = categoryTags;
    }

    /// <summary>
    /// Gets the source Items.
    /// </summary>
    public IReadOnlyList<ContextMenuItemViewModel> SourceItems { get; }

    public ContextMenuItemViewModel PrimaryItem => SourceItems[0];

    /// <summary>
    /// Gets the categories.
    /// </summary>
    public IReadOnlyList<ApprovalTagChipViewModel> CategoryTags { get; }

    public string DisplayName => PrimaryItem.DisplayName;

    public string KeyName => PrimaryItem.KeyName;

    public string Subtitle => PrimaryItem.Subtitle;

    public bool ShowKeyName => PrimaryItem.ShowKeyName;

    public string RegistryPath => PrimaryItem.RegistryPath;

    public string ShellPathTail => PrimaryItem.ShellPathTail;

    public string Notes => PrimaryItem.Notes;

    public bool ShowNotes => PrimaryItem.ShowNotes;

    public System.Windows.Media.ImageSource? IconSource => PrimaryItem.IconSource;

    public bool CanReviewApproval => SourceItems.Any(static item => item.CanReviewApproval);

    public bool IsWindows11ContextMenu => SourceItems.Any(static item => item.Entry.IsWindows11ContextMenu);

    public bool HasRegistryBackedItem => SourceItems.Any(static item => item.IsPresentInRegistry && !item.IsDeleted);

    public bool CanRemove => !IsWindows11ContextMenu;

    public string ApprovalRemoveConfirmationText => HasRegistryBackedItem
        ? _localization.Translate("ApprovalRemoveConfirmation")
        : _localization.Translate("ApprovalRemoveWithoutRegistryConfirmation");

    /// <summary>
    /// Gets or sets a value indicating whether approval Remove Flyout Open.
    /// </summary>
    [ObservableProperty]
    public partial bool IsApprovalRemoveFlyoutOpen { get; set; }
}

/// <summary>
/// Represents a tag chip shown on an approval card.
/// </summary>
public sealed class ApprovalTagChipViewModel
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ApprovalTagChipViewModel"/> class.
    /// </summary>
    public ApprovalTagChipViewModel(string text, bool isWindows11Tag)
    {
        Text = text;
        IsWindows11Tag = isWindows11Tag;
    }

    /// <summary>
    /// Gets the tag text.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets a value indicating whether the chip marks a Win11 new-menu item.
    /// </summary>
    public bool IsWindows11Tag { get; }
}
