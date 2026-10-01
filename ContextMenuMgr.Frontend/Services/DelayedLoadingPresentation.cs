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

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;
    private CancellationTokenSource? _transition;
    private DateTimeOffset _shownAt;
    private bool _isVisible;
    private bool _disposed;

    public DelayedLoadingPresentation(
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? now = null)
    {
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

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
                await _delay(ShowDelay, token);
                token.ThrowIfCancellationRequested();
                _shownAt = _now();
                IsVisible = true;
            }
            else if (IsVisible)
            {
                var remaining = MinimumVisible - (_now() - _shownAt);
                if (remaining > TimeSpan.Zero) await _delay(remaining, token);
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
