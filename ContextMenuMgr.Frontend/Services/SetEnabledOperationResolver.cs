using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

internal enum SetEnabledOutcome { Applied, NotApplied, Uncertain }

internal sealed record SetEnabledResolution(SetEnabledOutcome Outcome, ContextMenuEntry? Item);

internal static class SetEnabledOperationResolver
{
    public static async Task<SetEnabledResolution> ExecuteAsync(
        IBackendClient backend, ContextMenuEntry original, bool enable,
        TimeSpan mutationBudget, TimeSpan verificationBudget,
        CancellationToken shutdownToken = default)
    {
        shutdownToken.ThrowIfCancellationRequested();
        using var mutation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        mutation.CancelAfter(mutationBudget);
        FrontendDebugLog.Info("SetEnabledOperationResolver",
            $"SetEnabledRequestBudget: ItemId={original.Id}, RequestedEnabled={enable}, BackendRegistryPath={original.BackendRegistryPath}, RequestTimeoutMs={mutationBudget.TotalMilliseconds}.");
        try
        {
            var updated = await backend.SetEnabledAsync(original.Id, enable, mutation.Token, original)
                ?? throw new InvalidOperationException("The backend completed the request without returning the updated menu item.");
            return new SetEnabledResolution(SetEnabledOutcome.Applied, updated);
        }
        catch (Exception) when (shutdownToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(shutdownToken);
        }
        catch (Exception ex) when (!shutdownToken.IsCancellationRequested
                                   && (ex is TimeoutException
                                       || ex is OperationCanceledException && mutation.IsCancellationRequested))
        {
            FrontendDebugLog.Warning("SetEnabledOperationResolver",
                $"SetEnabledTimeout: ItemId={original.Id}, RequestedEnabled={enable}, BackendRegistryPath={original.BackendRegistryPath}, RequestTimeoutMs={mutationBudget.TotalMilliseconds}, Message={ex.Message}.");
        }

        FrontendDebugLog.Info("SetEnabledOperationResolver",
            $"SetEnabledOutcomeVerificationStarted: ItemId={original.Id}, RequestedEnabled={enable}, BackendRegistryPath={original.BackendRegistryPath}.");
        using var verification = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        verification.CancelAfter(verificationBudget);
        try
        {
            var authoritative = await backend.GetContextMenuItemStateAsync(original.Id, original, verification.Token);
            if (authoritative is { IsPresentInRegistry: true })
            {
                var outcome = authoritative.IsEnabled == enable
                    ? SetEnabledOutcome.Applied : SetEnabledOutcome.NotApplied;
                FrontendDebugLog.Info("SetEnabledOperationResolver",
                    $"{(outcome == SetEnabledOutcome.Applied ? "SetEnabledOutcomeVerifiedApplied" : "SetEnabledOutcomeVerifiedNotApplied")}: ItemId={original.Id}, RequestedEnabled={enable}, BackendRegistryPath={authoritative.BackendRegistryPath}.");
                return new SetEnabledResolution(outcome, authoritative);
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning("SetEnabledOperationResolver",
                $"SetEnabledOutcomeVerificationFailed: ItemId={original.Id}, RequestedEnabled={enable}, Message={ex.Message}.");
        }

        FrontendDebugLog.Warning("SetEnabledOperationResolver",
            $"SetEnabledOutcomeUncertain: ItemId={original.Id}, RequestedEnabled={enable}, BackendRegistryPath={original.BackendRegistryPath}.");
        return new SetEnabledResolution(SetEnabledOutcome.Uncertain, null);
    }
}
