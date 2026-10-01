using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ContextMenuToggleLateNotificationTests
{
    [Fact]
    public async Task BusyRing_RemainsActiveBeyondOldFiveSecondBudget()
    {
        var settings = new FrontendSettingsService(null,
            Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid() + ".json"));
        var localization = new LocalizationService(settings);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var item = new ContextMenuItemViewModel(new ContextMenuEntry
        {
            Id = @"*\shell|SlowTest", EntryKind = ContextMenuEntryKind.ShellVerb,
            IsEnabled = true, IsPresentInRegistry = true
        }, localization, null!, null!, (_, _) =>
        {
            started.TrySetResult();
            return complete.Task;
        });
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(item.IsToggleBusy) && !item.IsToggleBusy)
                idle.TrySetResult();
        };
        item.IsEnabled = false;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(TimeSpan.FromMilliseconds(5100));
        Assert.True(item.IsToggleBusy);
        Assert.False(item.CanToggle);
        complete.SetResult(true);
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(item.IsToggleBusy);
    }

    [Fact]
    public async Task LateAuthoritativeSuccess_IsNotOverwrittenByOlderFailedTask()
    {
        var settings = new FrontendSettingsService(null,
            Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid() + ".json"));
        var localization = new LocalizationService(settings);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new ContextMenuEntry
        {
            Id = @"*\shell|Test",
            EntryKind = ContextMenuEntryKind.ShellVerb,
            IsEnabled = true,
            IsPresentInRegistry = true
        };
        using var item = new ContextMenuItemViewModel(original, localization, null!, null!,
            (_, _) =>
            {
                requestStarted.TrySetResult();
                return finishRequest.Task;
            });
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(item.IsToggleBusy) && !item.IsToggleBusy)
                idle.TrySetResult();
        };

        item.IsEnabled = false;
        Assert.True(item.IsToggleBusy);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        item.Update(original with { IsEnabled = false });
        finishRequest.SetResult(false);
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(item.IsEnabled);
        Assert.False(item.IsToggleBusy);
    }
}
