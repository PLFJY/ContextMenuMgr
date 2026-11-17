using System.Reflection;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ApplicationGroupBatchTests
{
    // Permanent: filtered selection and delayed mutations must not expand the
    // batch, enable disabled items, or turn a stale delete into Undo Delete.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SelectionBatch_PreservesScopeAndRechecksRowsBeforeEachMutation(bool delete, bool restore)
    {
        var settings = new FrontendSettingsService(null, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json"));
        var localization = new LocalizationService(settings);
        var client = DispatchProxy.Create<IBackendClient, BatchBackend>();
        var backend = (BatchBackend)client;
        await using var workspace = new ContextMenuWorkspaceService(client, null!, null!, null!, null!, null!,
            new IconPreviewService(), localization, settings, null!, null!);
        ContextMenuItemViewModel Item(string name) => new(new ContextMenuEntry
        {
            Id = @"*\shell|" + name, KeyName = name, DisplayName = name,
            Category = ContextMenuCategory.File, EntryKind = ContextMenuEntryKind.ShellVerb,
            CommandText = "\"C:\\App\\app.exe\" \"%1\"", FilePath = @"C:\App\app.exe",
            IsEnabled = true, IsPresentInRegistry = true
        }, localization, new IconPreviewService(), null!) { IsBackendAvailable = true };
        using var first = Item("first");
        using var second = Item("second");
        using var hidden = Item("hidden");
        using var moved = Item("moved");
        using var addedLater = Item("added-later");
        if (restore)
            foreach (var item in new[] { first, second, hidden, moved })
                item.Update(item.Entry with { IsDeleted = true, HasBackup = true, DeletedAtUtc = DateTimeOffset.UtcNow });
        foreach (var item in new[] { first, second, hidden, moved }) workspace.Items.Add(item);
        foreach (var item in workspace.Items) backend.Entries[item.Id] = item.Entry;
        var identities = new ContextMenuApplicationIdentityService();
        using var page = new ApplicationGroupsPageViewModel(workspace, localization, new GlobalSearchNavigationFilterService(), identities);
        var group = new ApplicationGroupViewModel(identities.GetIdentity(first).Identity, [first, second],
            [first, second, hidden, moved], page, localization);
        page.Groups.Add(group);

        hidden.IsApplicationGroupChecked = true;
        moved.IsApplicationGroupChecked = true;
        group.ToggleSelectionCommand.Execute(null);
        Assert.Equal(true, group.IsAllVisibleChecked);
        group.ToggleSelectionCommand.Execute(null);
        Assert.False(first.IsApplicationGroupChecked);
        Assert.True(hidden.IsApplicationGroupChecked); // Filtered-out selections survive select-all changes.
        first.IsApplicationGroupChecked = true;
        Assert.Null(group.IsAllVisibleChecked);
        group.ToggleSelectionCommand.Execute(null);

        var operation = restore ? group.RestoreSelectedCommand.ExecuteAsync(null)
            : delete ? group.DeleteSelectedCommand.ExecuteAsync(null) : group.DisableSelectedCommand.ExecuteAsync(null);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(page.IsBulkBusy);
            Assert.False(group.ToggleSelectionCommand.CanExecute(null));
            Assert.False(group.DeleteSelectedCommand.CanExecute(null));
            Assert.False(group.DisableSelectedCommand.CanExecute(null));
            Assert.False(group.RestoreSelectedCommand.CanExecute(null));
            Assert.False(group.PermanentlyDeleteSelectedCommand.CanExecute(null));

            second.Update(restore ? second.Entry with { DeletedAtUtc = second.DeletedAtUtc!.Value.AddMinutes(1) }
                : delete ? second.Entry with { IsDeleted = true } : second.Entry with { IsEnabled = false });
            moved.Update(moved.Entry with { CommandText = "\"C:\\Other\\other.exe\" \"%1\"", FilePath = @"C:\Other\other.exe" });
            addedLater.IsApplicationGroupChecked = true;
            workspace.Items.Add(addedLater);
            group.UpdateItems([first, second, addedLater], [first, second, hidden, moved, addedLater]);
        }
        finally
        {
            backend.FirstResponse.TrySetResult(first.Entry);
        }
        await operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { first.Id, hidden.Id }, backend.Calls.Select(call => call.Id));
        Assert.All(backend.Calls, call => Assert.Equal(restore ? nameof(IBackendClient.UndoDeleteAsync)
            : delete ? nameof(IBackendClient.DeleteItemAsync) : nameof(IBackendClient.SetEnabledAsync), call.Command));
        Assert.All(backend.Calls, call => Assert.NotEqual(true, call.Enable));
        Assert.True(first.IsApplicationGroupChecked); // A completed request without changed state is not success.
        Assert.True(hidden.IsApplicationGroupChecked);
        Assert.True(addedLater.IsApplicationGroupChecked);
        Assert.False(page.IsBulkBusy);
    }

    public class BatchBackend : DispatchProxy
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ContextMenuEntry?> FirstResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Command, string Id, bool? Enable)> Calls { get; } = [];
        public Dictionary<string, ContextMenuEntry> Entries { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name.StartsWith("add_", StringComparison.Ordinal) || method.Name.StartsWith("remove_", StringComparison.Ordinal)) return null;
            if (method.Name == nameof(IBackendClient.DisposeAsync)) return ValueTask.CompletedTask;
            if (method.Name is nameof(IBackendClient.DeleteItemAsync) or nameof(IBackendClient.SetEnabledAsync) or nameof(IBackendClient.UndoDeleteAsync))
            {
                Calls.Add((method.Name, (string)args![0]!, method.Name == nameof(IBackendClient.SetEnabledAsync) ? (bool)args[1]! : null));
                if (Calls.Count == 1)
                {
                    Started.TrySetResult();
                    return FirstResponse.Task;
                }
                return Task.FromResult<ContextMenuEntry?>(Entries[(string)args[0]!]);
            }
            throw new NotSupportedException(method.Name);
        }
    }
}
