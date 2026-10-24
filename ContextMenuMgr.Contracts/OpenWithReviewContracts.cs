namespace ContextMenuMgr.Contracts;

public enum OpenWithReviewStatus { New, Changed, ReRegistered, Conflict, Uncertain, Reapplied, Missing, IsolationFailed }
public enum OpenWithReviewDecision { Accept, Reject }

// Identity is the physical Applications key, scoped to the authenticated pipe user.
// Registration retains raw command, capability and visibility provenance information.
public sealed record OpenWithPendingReview(
    string UserSid, string RegistryPath, string Generation, OpenWithReviewStatus Status,
    SpecialMenuEntry Registration, bool CanAccept, bool CanReject, string? Limitation,
    bool HasPersistentProtection, bool? IsEnabled, bool IsQuarantined = false);

public sealed record OpenWithReviewSnapshot(string UserSid, IReadOnlyList<OpenWithPendingReview> Reviews,
    bool BaselineEstablished = false);

public sealed record OpenWithReviewRequest(
    string RegistryPath, string Generation, OpenWithReviewDecision Decision);
