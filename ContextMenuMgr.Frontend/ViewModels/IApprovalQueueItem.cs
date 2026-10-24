namespace ContextMenuMgr.Frontend.ViewModels;

// Shared presentation only. Physical operations remain in their owning domain.
public interface IApprovalQueueItem
{
    string Id { get; }
    string DisplayName { get; }
    string Subtitle { get; }
    string RegistryPath { get; }
    string ShellPathTail { get; }
    string Notes { get; }
    IReadOnlyList<ApprovalTagChipViewModel> CategoryTags { get; }
    void RefreshLocalization();
}
