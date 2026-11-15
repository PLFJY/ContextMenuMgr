using System.IO;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

internal enum AcknowledgementOutcome { Applied, Rejected, Uncertain }
internal sealed record AcknowledgementResolution(AcknowledgementOutcome Outcome, ContextMenuEntry? Item, bool Removed = false);

internal static class AcknowledgeOperationResolver
{
    public static async Task<AcknowledgementResolution> ExecuteAsync(IBackendClient backend, ContextMenuEntry original,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            request.CancelAfter(BackendRequestBudgets.ClassicMutation);
            var item = await backend.AcknowledgeItemStateAsync(original.Id, request.Token, original);
            return new(AcknowledgementOutcome.Applied, item, item is null);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is OperationCanceledException or TimeoutException or IOException)
        {
            // A stopped wait is not a failed commit. Only read back; never resubmit.
            try
            {
                using var query = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                query.CancelAfter(BackendRequestBudgets.MutationOutcomeVerification);
                var current = await backend.GetContextMenuItemStateAsync(original.Id, original, query.Token);
                if (current is null) return new(AcknowledgementOutcome.Uncertain, null);
                return new(current.DetectedChangeKind == ContextMenuChangeKind.None && !current.IsPendingApproval
                    ? AcknowledgementOutcome.Applied : AcknowledgementOutcome.Rejected, current);
            }
            catch (BackendRequestException removed) when (removed.ErrorCode == PipeErrorCodes.MenuItemRemoved)
            {
                return new(AcknowledgementOutcome.Applied, null, Removed: true);
            }
            catch (Exception queryError) when (!cancellationToken.IsCancellationRequested)
            {
                FrontendDebugLog.Warning(nameof(AcknowledgeOperationResolver), $"Acknowledgement verification unavailable: ItemId={original.Id}, Error={queryError.Message}");
                return new(AcknowledgementOutcome.Uncertain, null);
            }
        }
    }
}
