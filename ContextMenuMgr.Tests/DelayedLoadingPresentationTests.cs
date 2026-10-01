using ContextMenuMgr.Frontend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class DelayedLoadingPresentationTests
{
    [Fact]
    public async Task FastLoad_NeverShowsIndicator()
    {
        var clock = new ManualDelay();
        using var presentation = clock.Create();
        var start = presentation.Update(true);
        await presentation.Update(false);
        clock.CompleteNext();
        await start;
        Assert.False(presentation.IsVisible);
    }

    [Fact]
    public async Task SlowLoad_ShowsThenRespectsMinimumVisibleTime()
    {
        var clock = new ManualDelay();
        using var presentation = clock.Create();
        var start = presentation.Update(true);
        Assert.False(presentation.IsVisible);
        Assert.Equal(DelayedLoadingPresentation.ShowDelay, clock.NextDuration);
        clock.AdvanceAndCompleteNext(DelayedLoadingPresentation.ShowDelay);
        await start;
        Assert.True(presentation.IsVisible);

        var finish = presentation.Update(false);
        Assert.True(presentation.IsVisible);
        Assert.Equal(DelayedLoadingPresentation.MinimumVisible, clock.NextDuration);
        clock.AdvanceAndCompleteNext(DelayedLoadingPresentation.MinimumVisible);
        await finish;
        Assert.False(presentation.IsVisible);
    }

    [Fact]
    public async Task CanceledOrOlderGeneration_CannotShowOverNewContent()
    {
        var clock = new ManualDelay();
        using var presentation = clock.Create();
        var old = presentation.Update(true);
        await presentation.Update(false);
        await old;
        Assert.False(presentation.IsVisible);

        var current = presentation.Update(true);
        clock.CompleteNext(); // Canceled delay from the older generation.
        Assert.False(presentation.IsVisible);
        clock.AdvanceAndCompleteNext(DelayedLoadingPresentation.ShowDelay);
        await current;
        Assert.True(presentation.IsVisible);
    }

    private sealed class ManualDelay
    {
        private readonly Queue<(TimeSpan Duration, TaskCompletionSource Source)> _pending = new();
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public TimeSpan NextDuration => _pending.Peek().Duration;

        public DelayedLoadingPresentation Create() => new(Delay, () => _now);

        private Task Delay(TimeSpan duration, CancellationToken token)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => source.TrySetCanceled(token));
            _pending.Enqueue((duration, source));
            return source.Task;
        }

        public void CompleteNext() => _pending.Dequeue().Source.TrySetResult();

        public void AdvanceAndCompleteNext(TimeSpan duration)
        {
            _now += duration;
            CompleteNext();
        }
    }
}
