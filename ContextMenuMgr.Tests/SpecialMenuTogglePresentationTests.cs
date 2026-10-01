using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class SpecialMenuTogglePresentationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Toggle_BusyPreventsDuplicateAndCompletesOrReverts(bool succeeds)
    {
        var settings = new FrontendSettingsService(null,
            Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid() + ".json"));
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var busyBeforeEnabledNotification = false;
        var item = new SpecialMenuItemViewModel(
            new SpecialMenuEntry { Id = "test", Kind = SpecialMenuKind.SendTo, DisplayName = "Test", IsEnabled = false },
            new IconPreviewService(),
            new LocalizationService(settings),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                invoked.TrySetResult();
                return completion.Task;
            });
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(item.IsEnabled) && item.IsEnabled)
                busyBeforeEnabledNotification = item.IsBusy;
            if (e.PropertyName == nameof(item.IsBusy) && !item.IsBusy) idle.TrySetResult();
        };

        item.IsEnabled = true;
        Assert.True(item.IsBusy);
        Assert.True(busyBeforeEnabledNotification);
        Assert.False(item.CanToggle);
        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        item.IsEnabled = false; // An overlapping source update must not start another write.
        Assert.True(item.IsEnabled);
        Assert.Equal(1, calls);

        completion.SetResult(succeeds);
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(item.IsBusy);
        Assert.Equal(succeeds, item.IsEnabled);
    }
}
