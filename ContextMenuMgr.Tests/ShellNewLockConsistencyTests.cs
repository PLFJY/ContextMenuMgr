using System.Reflection;
using System.Windows;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using Xunit;

namespace ContextMenuMgr.Tests;

// Permanent: a lost mutation response, stale snapshot metadata and repeated clicks
// must not turn a physically locked key into a false final Unlocked UI state.
public sealed class ShellNewLockConsistencyTests
{
    [Fact]
    public async Task LostResponse_UsesIndependentStateAndKeepsUnknownExplicit()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await VerifyScenario(queryFails: false);
                    await VerifyScenario(queryFails: true);
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally { app.Shutdown(); }
            });
            app.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
    }

    private static async Task VerifyScenario(bool queryFails)
    {
        var client = DispatchProxy.Create<IBackendClient, Backend>();
        var backend = (Backend)client;
        var settings = new FrontendSettingsService(null, Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid().ToString("N"), "settings.json"));
        using var page = new ShellNewPageViewModel(client, new IconPreviewService(), new LocalizationService(settings), new ExplorerRestartStateService());
        await page.RefreshAsync();
        Assert.False(page.IsShellNewOrderLocked);
        Assert.True(page.CanToggleShellNewOrderLock);
        page.IsShellNewOrderLocked = true;
        Assert.True(page.IsShellNewLockBusy);
        Assert.False(page.CanToggleShellNewOrderLock);
        // An attempted second click cannot enqueue another mutation.
        page.IsShellNewOrderLocked = false;
        Assert.True(page.IsShellNewOrderLocked);
        Assert.Equal(1, backend.Mutations);
        backend.QueryFails = queryFails;
        backend.MutationCompleted.SetException(new TimeoutException("Response pipe disconnected after backend commit."));
        for (var i = 0; i < 500 && page.IsShellNewLockBusy; i++) await Task.Delay(5);
        Assert.False(page.IsShellNewLockBusy);
        if (queryFails)
        {
            Assert.Null(page.IsShellNewOrderLocked);
            Assert.False(page.CanToggleShellNewOrderLock);
            Assert.False(string.IsNullOrWhiteSpace(page.StatusText));
            page.IsShellNewOrderLocked = false;
            Assert.Null(page.IsShellNewOrderLocked);
        }
        else
        {
            await page.RefreshAsync();
            Assert.True(page.IsShellNewOrderLocked);
            Assert.True(page.CanToggleShellNewOrderLock);
            Assert.True(backend.Queries >= 2);
        }
        Assert.Equal(1, backend.Mutations);
    }

    public class Backend : DispatchProxy
    {
        public TaskCompletionSource MutationCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool QueryFails { get; set; }
        public int Mutations { get; private set; }
        public int Queries { get; private set; }
        private bool _locked;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "add_NotificationReceived": case "remove_NotificationReceived": return null;
                case nameof(IBackendClient.GetShellNewOrderLockAsync):
                    Queries++;
                    return QueryFails ? Task.FromException<bool>(new IOException("Service unavailable.")) : Task.FromResult(_locked);
                case nameof(IBackendClient.SetShellNewOrderLockAsync):
                    Mutations++;
                    _locked = (bool)args![0]!;
                    return MutationCompleted.Task;
                case nameof(IBackendClient.GetSpecialMenuSnapshotAsync):
                    // Old/missing lock metadata must never decide the lock state.
                    return Task.FromResult<IReadOnlyList<SpecialMenuEntry>>([new SpecialMenuEntry
                    {
                        Id = "ShellNew:Separator", Kind = SpecialMenuKind.ShellNew,
                        Metadata = new Dictionary<string, string> { ["EntryType"] = "Separator", ["OrderLocked"] = "False" },
                        CanEdit = false, CanDelete = false, CanMove = false
                    }]);
                case nameof(IBackendClient.GetOfficeSuiteCoexistenceStatusAsync):
                    return Task.FromResult<OfficeSuiteCoexistenceStatus?>(null);
                default: throw new NotSupportedException(method?.Name);
            }
        }
    }
}
