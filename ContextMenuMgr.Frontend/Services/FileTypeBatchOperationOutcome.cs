namespace ContextMenuMgr.Frontend.Services;

public enum FileTypeBatchOperationStatus
{
    ConfirmedSuccess,
    ConfirmedFailure,
    Skipped,
    Cancelled,
    Uncertain
}

public sealed record FileTypeBatchOperationOutcome(FileTypeBatchOperationStatus Status, string? ErrorCode = null);
