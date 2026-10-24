using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the approval Queue Item View Model.
/// </summary>
public partial class ApprovalQueueItemViewModel : ObservableObject, IApprovalQueueItem, IDisposable
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
        CategoryTags = BuildCategoryTags();
        foreach (var item in SourceItems) item.PropertyChanged += SourceChanged;
    }

    private IReadOnlyList<ApprovalTagChipViewModel> BuildCategoryTags()
    {
        var tags = SourceItems.Select(item => item.Category).Distinct()
            .Select(category => new ApprovalTagChipViewModel(ContextMenuCategoryText.GetLocalizedName(category, _localization), false)).ToList();
        if (IsWindows11ContextMenu) tags.Insert(0, new ApprovalTagChipViewModel(_localization.Translate("Windows11PendingApprovalTag"), true));
        return tags;
    }
    private void SourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
        if (e.PropertyName is nameof(ContextMenuItemViewModel.Category) or nameof(ContextMenuItemViewModel.IsWindows11ContextMenu)) RefreshLocalization();
        if (e.PropertyName is nameof(ContextMenuItemViewModel.IsPendingApproval) or nameof(ContextMenuItemViewModel.IsToggleBusy))
            OnPropertyChanged(nameof(CanReviewApproval));
    }
    public void RefreshLocalization()
    { CategoryTags = BuildCategoryTags(); OnPropertyChanged(nameof(CategoryTags)); }
    public void Dispose() { foreach (var item in SourceItems) item.PropertyChanged -= SourceChanged; }

    /// <summary>
    /// Gets the source Items.
    /// </summary>
    public IReadOnlyList<ContextMenuItemViewModel> SourceItems { get; private set; }

    public void UpdateSources(IReadOnlyList<ContextMenuItemViewModel> sourceItems)
    {
        if (SourceItems.SequenceEqual(sourceItems)) return;
        foreach (var item in SourceItems) item.PropertyChanged -= SourceChanged;
        SourceItems = sourceItems;
        foreach (var item in SourceItems) item.PropertyChanged += SourceChanged;
        CategoryTags = BuildCategoryTags();
        OnPropertyChanged(string.Empty);
    }

    public ContextMenuItemViewModel PrimaryItem => SourceItems[0];

    public string Id => ContextMenuMgr.Contracts.ContextMenuApprovalIdentity.CreateLogicalItemKey(PrimaryItem.Entry);

    /// <summary>
    /// Gets the categories.
    /// </summary>
    public IReadOnlyList<ApprovalTagChipViewModel> CategoryTags { get; private set; }

    public string DisplayName => PrimaryItem.DisplayName;

    public string KeyName => PrimaryItem.KeyName;

    public string Subtitle => PrimaryItem.Subtitle;

    public bool ShowKeyName => PrimaryItem.ShowKeyName;

    public string RegistryPath => PrimaryItem.RegistryPath;

    public string ShellPathTail => PrimaryItem.ShellPathTail;

    public string Notes => PrimaryItem.Notes;

    public string? NotesToolTip => PrimaryItem.NotesToolTip;

    public bool ShowNotes => PrimaryItem.ShowNotes;

    public System.Windows.Media.ImageSource? IconSource => PrimaryItem.IconSource;

    public bool CanReviewApproval => !IsDecisionBusy && SourceItems.Any(static item => item.CanReviewApproval);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReviewApproval))]
    public partial bool IsDecisionBusy { get; set; }

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
