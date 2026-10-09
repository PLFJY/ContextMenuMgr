using System.ComponentModel;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Presents a real load only after a short delay, and keeps a shown indicator readable.
/// Call Update on the UI thread. Cancellation also protects a reused page from old loads.
/// </summary>
public sealed class DelayedLoadingPresentation : INotifyPropertyChanged, IDisposable
{
    public static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(140);
    public static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _transition;
    private DateTimeOffset _shownAt;
    private bool _isVisible;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }

    public Task Update(bool isLoading)
    {
        if (_disposed) return Task.CompletedTask;
        _transition?.Cancel();
        _transition?.Dispose();
        var transition = _transition = new CancellationTokenSource();
        return TransitionAsync(isLoading, transition.Token);
    }

    private async Task TransitionAsync(bool isLoading, CancellationToken token)
    {
        try
        {
            if (isLoading)
            {
                if (IsVisible) return;
                await Task.Delay(ShowDelay, token);
                token.ThrowIfCancellationRequested();
                _shownAt = DateTimeOffset.UtcNow;
                IsVisible = true;
            }
            else if (IsVisible)
            {
                var remaining = MinimumVisible - (DateTimeOffset.UtcNow - _shownAt);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
                token.ThrowIfCancellationRequested();
                IsVisible = false;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transition?.Cancel();
        _transition?.Dispose();
    }
}
