using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public sealed class ToastNotificationViewModel
{
    private readonly LocalizationService _localization;

    public ToastNotificationViewModel(BackendNotification notification, LocalizationService localization)
    {
        Notification = notification;
        _localization = localization;
    }

    public BackendNotification Notification { get; }

    public string ItemId => Notification.Item?.Id ?? string.Empty;

    public string Title => Notification.Item?.DisplayName ?? "Context menu update";

    public string Message => Notification.Message;

    public string RegistryPath => Notification.Item?.RegistryPath ?? _localization.Translate("NoRegistryPath");

    public bool IsApprovalRequest => Notification.Kind == PipeNotificationKind.ItemDetected
        && Notification.Item is { IsPendingApproval: true };

    public string AllowText => _localization.Translate("Allow");

    public string DenyText => _localization.Translate("Deny");
}
