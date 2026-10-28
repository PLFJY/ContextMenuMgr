using System.Collections.ObjectModel;
using System.Security.Principal;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.ViewModels;

namespace ContextMenuMgr.Frontend.Services;

// One coordinator for central approvals: refreshes cannot overtake decisions or erase failed reads.
public partial class OpenWithReviewQueueService(IBackendClient backend, LocalizationService localization,
    CancellationToken lifetime = default) : ObservableObject
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly string _sid = WindowsIdentity.GetCurrent().User!.Value;
    private Task<bool>? _refresh;
    private bool _invalidated;
    public ObservableCollection<OpenWithApprovalItemViewModel> Items { get; } = [];
    public event EventHandler? SnapshotChanged;
    [ObservableProperty] public partial string StatusText { get; private set; } = string.Empty;

    public Task<bool> RefreshAsync(CancellationToken token)
    {
        if (!_dispatcher.CheckAccess()) return _dispatcher.InvokeAsync(() => RefreshAsync(token)).Task.Unwrap();
        _invalidated = true;
        if (_refresh is { IsCompleted: false }) return _refresh;
        return _refresh = RefreshCoreAsync(token);
    }

    private async Task<bool> RefreshCoreAsync(CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        token = operation.Token;
        var held = false;
        try
        {
            await _gate.WaitAsync(token);
            held = true;
            do
            {
                _invalidated = false;
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                budget.CancelAfter(TimeSpan.FromSeconds(20));
                Apply(await backend.GetOpenWithPendingReviewsAsync(budget.Token));
            } while (_invalidated);
            StatusText = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            StatusText = localization.Translate("OpenWithReviewRefreshFailed");
            foreach (var item in Items) item.IsBackendAvailable = false;
            FrontendDebugLog.Warning(nameof(OpenWithReviewQueueService), $"Pending review refresh failed: {ex.Message}");
            return false;
        }
        finally { if (held) _gate.Release(); }
    }

    public async Task DecideAsync(OpenWithApprovalItemViewModel item, OpenWithReviewDecision decision, CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        token = operation.Token;
        if (decision == OpenWithReviewDecision.Accept ? !item.CanAccept : !item.CanReject) return;
        var submitted = item.Review;
        item.IsBusy = true;
        var held = false;
        try
        {
            await _gate.WaitAsync(token);
            held = true;
            // A refresh queued before the click may already have replaced this generation.
            if (!Items.Contains(item) || item.Review.Generation != submitted.Generation)
            {
                StatusText = localization.Translate("OpenWithStaleReview");
                return;
            }
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(BackendRequestBudgets.ApprovalDecision);
            Apply(await backend.DecideOpenWithReviewAsync(new(submitted.RegistryPath, submitted.Generation, decision), budget.Token));
            StatusText = string.Empty;
        }
        catch (Exception ex)
        {
            item.IsStateUncertain = true;
            try
            {
                // Timeout stops waiting. A separate query waits behind the backend's decision gate.
                using var verification = CancellationTokenSource.CreateLinkedTokenSource(token);
                verification.CancelAfter(BackendRequestBudgets.MutationOutcomeVerification);
                Apply(await backend.GetOpenWithPendingReviewsAsync(verification.Token));
                StatusText = localization.Translate(Items.Contains(item)
                    ? ex.Message == "OpenWithStaleReview" ? "OpenWithStaleReview" : "OpenWithReviewDecisionFailed"
                    : "OpenWithReviewDecisionChecked");
            }
            catch
            {
                StatusText = localization.Translate("OpenWithOperationUncertain");
                item.IsStateUncertain = true;
            }
            FrontendDebugLog.Warning(nameof(OpenWithReviewQueueService), $"Review decision failed; reconciled independently: {ex.Message}");
        }
        finally { item.IsBusy = false; if (held) _gate.Release(); }
    }

    private void Apply(OpenWithReviewSnapshot snapshot)
    {
        if (!snapshot.UserSid.Equals(_sid, StringComparison.OrdinalIgnoreCase)
            || snapshot.Reviews.Count > 8192 || snapshot.Reviews.Any(r => !r.UserSid.Equals(_sid, StringComparison.OrdinalIgnoreCase)
                || !(r.RegistryPath.StartsWith($@"HKEY_USERS\{_sid}\Software\Classes\Applications\", StringComparison.OrdinalIgnoreCase)
                    || r.RegistryPath.StartsWith(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Applications\", StringComparison.OrdinalIgnoreCase)))
            || snapshot.Reviews.Select(r => r.RegistryPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.Reviews.Count)
            throw new InvalidOperationException("Open With review scope mismatch.");
        var existing = Items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        var next = snapshot.Reviews.Where(r => r.Status is OpenWithReviewStatus.New or OpenWithReviewStatus.Changed
            or OpenWithReviewStatus.ReRegistered or OpenWithReviewStatus.Conflict or OpenWithReviewStatus.Uncertain).Select(review =>
        {
            if (!existing.TryGetValue(review.RegistryPath, out var item)) item = new(review, localization);
            item.Apply(review);
            item.IsBackendAvailable = true;
            return item;
        }).ToArray();
        IncrementalCollection.Apply(Items, next);
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Invalidate(BackendNotification notification, CancellationToken token)
    {
        if (notification.SpecialKind != SpecialMenuKind.OpenWith
            || !string.Equals(notification.UserSid, _sid, StringComparison.OrdinalIgnoreCase)) return false;
        _ = RefreshAsync(token); // payloads and their arrival order never populate the queue
        return true;
    }
}
