using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using System.Reflection;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ApprovalsBulkSelectionTests
{
    // Permanent: bulk review selection lives on the row view model (survives search filtering),
    // gates the bulk commands, and applies a decision only to checked rows through the workspace entry point.
    [Fact]
    public async Task BulkSelection_GatesCommands_SurvivesFiltering_AndActsOnlyOnCheckedRows()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                var directory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid().ToString("N"));
                try
                {
                    var client = DispatchProxy.Create<IBackendClient, DecisionRecordingBackend>();
                    var backend = (DecisionRecordingBackend)client;
                    var settings = new FrontendSettingsService(null, Path.Combine(directory, "settings.json"));
                    var localization = new LocalizationService(settings);
                    var icons = new IconPreviewService();
                    var manager = DispatchProxy.Create<IBackendServiceManager, StubServiceManager>();
                    await using var workspace = new ContextMenuWorkspaceService(client, manager, null!, null!, null!, null!,
                        icons, localization, settings, null!, null!);
                    using var alpha = new ContextMenuItemViewModel(new ContextMenuEntry
                    {
                        Id = "classic|alpha", DisplayName = "Alpha Handler", KeyName = "alpha",
                        RegistryPath = @"HKEY_CLASSES_ROOT\*\shell\alpha", IsPendingApproval = true
                    }, localization, icons, null!);
                    using var beta = new ContextMenuItemViewModel(new ContextMenuEntry
                    {
                        Id = "classic|beta", DisplayName = "Beta Handler", KeyName = "beta",
                        RegistryPath = @"HKEY_CLASSES_ROOT\*\shell\beta", IsPendingApproval = true
                    }, localization, icons, null!);
                    workspace.Items.Add(alpha);
                    workspace.Items.Add(beta);
                    await workspace.RefreshAsync(); // activates UI state so decision upserts apply
                    using var page = new ApprovalsPageViewModel(workspace, localization, new FrontendNavigationState());
                    var raisedProperties = new List<string>();
                    var allowCanExecuteChanged = 0;
                    var denyCanExecuteChanged = 0;
                    var toggleCanExecuteChanged = 0;
                    page.PropertyChanged += (_, e) => { if (e.PropertyName is not null) raisedProperties.Add(e.PropertyName); };
                    page.AllowSelectedCommand.CanExecuteChanged += (_, _) => allowCanExecuteChanged++;
                    page.DenySelectedCommand.CanExecuteChanged += (_, _) => denyCanExecuteChanged++;
                    page.ToggleAllFilteredCommand.CanExecuteChanged += (_, _) => toggleCanExecuteChanged++;

                    Assert.Equal(2, page.Items.Count);
                    Assert.False(page.AllowSelectedCommand.CanExecute(null));
                    Assert.Equal(0, page.SelectedCount);
                    Assert.Equal(string.Empty, page.SelectedCountText);
                    Assert.Equal(false, page.IsAllFilteredChecked);
                    Assert.True(page.ToggleAllFilteredCommand.CanExecute(null));

                    var alphaRow = Assert.Single(page.Items.OfType<ApprovalQueueItemViewModel>(),
                        row => row.DisplayName == "Alpha Handler");
                    var betaRow = Assert.Single(page.Items.OfType<ApprovalQueueItemViewModel>(),
                        row => row.DisplayName == "Beta Handler");
                    var allowChangesBeforeCheck = allowCanExecuteChanged;
                    var denyChangesBeforeCheck = denyCanExecuteChanged;
                    raisedProperties.Clear();
                    alphaRow.IsChecked = true;
                    Assert.Contains(nameof(ApprovalsPageViewModel.SelectedCount), raisedProperties);
                    Assert.Contains(nameof(ApprovalsPageViewModel.SelectedCountText), raisedProperties);
                    Assert.Contains(nameof(ApprovalsPageViewModel.IsAllFilteredChecked), raisedProperties);
                    Assert.True(allowCanExecuteChanged > allowChangesBeforeCheck);
                    Assert.True(denyCanExecuteChanged > denyChangesBeforeCheck);
                    Assert.True(page.AllowSelectedCommand.CanExecute(null));
                    Assert.True(page.DenySelectedCommand.CanExecute(null));
                    Assert.Equal(1, page.SelectedCount);
                    Assert.Contains("1", page.SelectedCountText);
                    Assert.Null(page.IsAllFilteredChecked); // partial selection

                    page.ToggleAllFilteredCommand.Execute(null);
                    Assert.True(alphaRow.IsChecked);
                    Assert.True(betaRow.IsChecked);
                    Assert.True(page.IsAllFilteredChecked);
                    Assert.Equal(2, page.SelectedCount);

                    var propertiesBeforeFilter = raisedProperties.Count;
                    var toggleChangesBeforeFilter = toggleCanExecuteChanged;
                    page.SearchText = "Beta";
                    Assert.Contains(nameof(ApprovalsPageViewModel.IsAllFilteredChecked),
                        raisedProperties.Skip(propertiesBeforeFilter));
                    Assert.True(toggleCanExecuteChanged > toggleChangesBeforeFilter);
                    Assert.Single(page.ItemsView.Cast<IApprovalQueueItem>());
                    Assert.Equal(2, page.SelectedCount); // hidden checked rows keep their selection
                    Assert.Equal(true, page.IsAllFilteredChecked); // every visible row is checked

                    page.ToggleAllFilteredCommand.Execute(null);
                    Assert.False(betaRow.IsChecked); // master acts on the visible row only
                    Assert.True(alphaRow.IsChecked);
                    Assert.Equal(false, page.IsAllFilteredChecked);
                    Assert.Equal(1, page.SelectedCount);

                    page.SearchText = "no such item";
                    Assert.Empty(page.ItemsView.Cast<IApprovalQueueItem>());
                    Assert.Equal(false, page.IsAllFilteredChecked);
                    Assert.False(page.ToggleAllFilteredCommand.CanExecute(null));
                    Assert.True(page.AllowSelectedCommand.CanExecute(null)); // hidden checked rows stay actionable
                    page.SearchText = "Beta";
                    Assert.Single(page.ItemsView.Cast<IApprovalQueueItem>());
                    Assert.Equal(false, page.IsAllFilteredChecked);
                    Assert.Equal(1, page.SelectedCount);

                    await page.AllowSelectedCommand.ExecuteAsync(null);

                    Assert.Equal("classic|alpha", Assert.Single(backend.DecidedItemIds));
                    Assert.Equal(0, page.SelectedCount);
                    Assert.Equal(string.Empty, page.SelectedCountText);
                    Assert.False(page.AllowSelectedCommand.CanExecute(null));
                    Assert.Single(page.Items);
                    Assert.Equal(false, page.IsAllFilteredChecked);
                    Assert.DoesNotContain(page.Items, row => row.IsChecked);
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                    dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal);
                }
            });
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
    }

    public class DecisionRecordingBackend : DispatchProxy
    {
        public List<string> DecidedItemIds { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name.StartsWith("add_", StringComparison.Ordinal) == true
                || method?.Name.StartsWith("remove_", StringComparison.Ordinal) == true) return null;
            if (method?.Name == nameof(IBackendClient.DisposeAsync)) return ValueTask.CompletedTask;
            if (method?.Name == nameof(IBackendClient.PingAsync)) return Task.CompletedTask;
            if (method?.Name is nameof(IBackendClient.GetSnapshotAsync)
                or nameof(IBackendClient.GetWpsOfficePendingApprovalsAsync))
                return Task.FromException<IReadOnlyList<ContextMenuEntry>>(new IOException("Data source unavailable in test."));
            if (method?.Name == nameof(IBackendClient.ApplyDecisionAsync))
            {
                var itemId = (string)args![0]!;
                DecidedItemIds.Add(itemId);
                return Task.FromResult<ContextMenuEntry?>(new ContextMenuEntry
                {
                    Id = itemId, IsPendingApproval = false, IsEnabled = true
                });
            }
            throw new NotSupportedException(method?.Name);
        }
    }

    public class StubServiceManager : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.ReturnType == typeof(bool)) return false;
            return null;
        }
    }
}
