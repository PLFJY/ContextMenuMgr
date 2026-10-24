using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class OpenWithApprovalItemViewModel(OpenWithPendingReview review, LocalizationService localization)
    : ObservableObject, IApprovalQueueItem
{
    public OpenWithPendingReview Review { get; private set; } = review;
    public string Id => Review.RegistryPath;
    public string DisplayName => Review.Registration.DisplayName;
    public string RegistryPath => Review.RegistryPath;
    public string ShellPathTail => Review.Registration.TargetPath ?? "";
    public string Subtitle => Review.Registration.CommandText ?? ShellPathTail;
    public string Notes => localization.Translate("OpenWithMonitor" + Review.Status) + " · "
        + localization.Translate("OpenWithScope" + (RegistryPath.StartsWith("HKEY_USERS\\", StringComparison.OrdinalIgnoreCase) ? "User" : "Machine"))
        + " · " + localization.Translate(Review.IsEnabled is { } enabled ? enabled ? "ToggleOn" : "ToggleOff" : "OpenWithVisibilityUnverified");
    public string Explanation => IsStateUncertain ? localization.Translate("OpenWithOperationUncertain")
        : Review.Limitation is { Length: > 0 } reason ? localization.Translate(reason)
        : localization.Translate(Review.IsQuarantined ? "OpenWithReviewNotice" : "OpenWithReviewPreserveDisabled");
    public IReadOnlyList<ApprovalTagChipViewModel> CategoryTags => [new(localization.Translate("OpenWithPageTitle"), false)];
    public bool CanAccept => IsBackendAvailable && Review.CanAccept && !IsBusy && !IsStateUncertain;
    public bool CanReject => IsBackendAvailable && Review.CanReject && !IsBusy && !IsStateUncertain;
    public string AcceptText => localization.Translate(Review.IsQuarantined ? "OpenWithAllowRegistration" : "OpenWithAcceptRegistration");
    public string RejectText => localization.Translate("OpenWithRejectRegistration");
    public string InspectText => localization.Translate("OpenWithInspectRegistration");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    [NotifyPropertyChangedFor(nameof(CanReject))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    [NotifyPropertyChangedFor(nameof(CanReject))]
    [NotifyPropertyChangedFor(nameof(Explanation))]
    public partial bool IsStateUncertain { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    [NotifyPropertyChangedFor(nameof(CanReject))]
    public partial bool IsBackendAvailable { get; set; } = true;

    public void Apply(OpenWithPendingReview updated)
    {
        Review = updated;
        IsStateUncertain = updated.Status == OpenWithReviewStatus.Uncertain;
        OnPropertyChanged(string.Empty);
    }

    public void RefreshLocalization() => OnPropertyChanged(string.Empty);
}
