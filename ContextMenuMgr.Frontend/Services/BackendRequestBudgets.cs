namespace ContextMenuMgr.Frontend.Services;

internal static class BackendRequestBudgets
{
    public static readonly TimeSpan Windows11Mutation = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan Windows11Snapshot = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan ClassicMutation = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MutationOutcomeVerification = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ApprovalDecision = TimeSpan.FromSeconds(45);
}
