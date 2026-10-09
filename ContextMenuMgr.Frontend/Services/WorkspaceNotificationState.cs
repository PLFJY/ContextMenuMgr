using System.Collections.ObjectModel;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.ViewModels;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>Tracks notification baselines separately from workspace items and backend availability.</summary>
internal sealed class WorkspaceNotificationState(LocalizationService localization)
{
    private readonly PendingApprovalBaseline _regular = new();
    private readonly PendingApprovalBaseline _wpsOffice = new();
    private readonly HashSet<string> _seenChangedIds = new(StringComparer.OrdinalIgnoreCase);

    internal ObservableCollection<ToastNotificationViewModel> Notifications { get; } = [];

    internal event EventHandler<ContextMenuEntry>? PendingApprovalDetected;

    internal void UpdateWpsOffice(IReadOnlyList<ContextMenuEntry> snapshot)
        => UpdatePending(_wpsOffice, snapshot);

    internal void UpdateRegular(IReadOnlyList<ContextMenuEntry> snapshot)
    {
        var currentPendingIds = UpdatePending(_regular, snapshot);
        foreach (var item in snapshot.Where(static item => item.DetectedChangeKind != ContextMenuChangeKind.None))
        {
            if (_seenChangedIds.Add(item.Id))
            {
                Notifications.Insert(0, new ToastNotificationViewModel(
                    new BackendNotification
                    {
                        Kind = PipeNotificationKind.ItemStateChanged,
                        Item = item,
                        Message = localization.Format(
                            "StartupChangeNotificationFormat",
                            ContextMenuCategoryText.GetLocalizedName(item.Category, localization),
                            item.DisplayName)
                    },
                    localization));
            }
        }

        var currentChangedIds = snapshot
            .Where(static item => item.DetectedChangeKind != ContextMenuChangeKind.None)
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _seenChangedIds.IntersectWith(currentChangedIds);

        foreach (var toast in Notifications.Where(toast =>
                     !currentPendingIds.Contains(toast.ItemId) && !currentChangedIds.Contains(toast.ItemId)).ToList())
        {
            Notifications.Remove(toast);
        }
    }

    internal void Observe(BackendNotification notification)
    {
        if (notification.Kind == PipeNotificationKind.ItemDetected
            && notification.Item is { IsPendingApproval: true } item
            && _regular.SeenIds.Add(item.Id))
        {
            PendingApprovalDetected?.Invoke(this, item);
        }
    }

    internal void RemoveApproval(string itemId)
    {
        _regular.SeenIds.Remove(itemId);
        foreach (var toast in Notifications.Where(toast => toast.IsApprovalRequest
                     && string.Equals(toast.ItemId, itemId, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            Notifications.Remove(toast);
        }
    }

    private HashSet<string> UpdatePending(PendingApprovalBaseline baseline, IReadOnlyList<ContextMenuEntry> snapshot)
    {
        var pending = snapshot.Where(static item => item.IsPendingApproval).ToArray();
        var ids = pending.Select(static item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!baseline.Initialized)
        {
            baseline.SeenIds.UnionWith(ids);
            baseline.Initialized = true;
        }
        else
        {
            foreach (var item in pending)
            {
                if (baseline.SeenIds.Add(item.Id))
                {
                    PendingApprovalDetected?.Invoke(this, item);
                }
            }
        }

        baseline.SeenIds.IntersectWith(ids);
        return ids;
    }

    private sealed class PendingApprovalBaseline
    {
        internal bool Initialized { get; set; }
        internal HashSet<string> SeenIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
