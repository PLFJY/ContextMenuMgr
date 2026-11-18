using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

internal static class DocumentIconRepairFeedback
{
    public static string GetMessage(Exception error, LocalizationService localization)
    {
        var key = error is BackendRequestException request ? request.ErrorCode switch
        {
            PipeErrorCodes.DocumentIconRepairSourceUnavailable => "DocumentIconRepairSourceUnavailable",
            PipeErrorCodes.DocumentIconRepairConflict => "DocumentIconRepairConflict",
            PipeErrorCodes.DocumentIconRepairRecoveryInvalid => "DocumentIconRepairRecoveryInvalid",
            PipeErrorCodes.DocumentIconRepairUnavailable => "DocumentIconRepairUnavailable",
            PipeErrorCodes.DocumentIconRepairAccessDenied => "DocumentIconRepairAccessDenied",
            PipeErrorCodes.DocumentIconRepairMustRestore => "DocumentIconRepairMustRestore",
            _ => "DocumentIconRepairFailed"
        } : error is TimeoutException or OperationCanceledException or System.IO.IOException
            ? "DocumentIconRepairUncertain" : "DocumentIconRepairFailed";
        return localization.Translate(key);
    }
}
