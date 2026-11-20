using System.Reflection;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using Xunit;

namespace ContextMenuMgr.Tests;

// Permanent: late responses, authority after uncertain writes and restart intent.
// Mock transport verifies frontend coordination, never real Explorer behavior.
public sealed class Win11ClassicContextMenuFrontendTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InFlightSetting_PreventsDuplicateWrites_AndOnlyVerifiedChangeRequestsRestart(bool changed)
    {
        await using var fixture = new Fixture();
        fixture.Page.IsWin11ContextMenuSettingEnabled = true;
        fixture.Page.Win11ModernContextMenuDisabled = true;
        Assert.Equal(1, fixture.Backend.Writes);
        Assert.False(fixture.Settings.Current.Win11ModernContextMenuDisabled);
        Assert.False(fixture.Restart.NeedsRestart);
        fixture.Page.Win11ModernContextMenuDisabled = false;
        fixture.Page.Win11ModernContextMenuDisabled = true;
        Assert.Equal(1, fixture.Backend.Writes);
        fixture.Backend.Completion.SetResult(changed);
        await fixture.WaitForSaved(true);
        Assert.True(fixture.Page.Win11ModernContextMenuDisabled);
        Assert.Equal(changed, fixture.Restart.NeedsRestart);
        Assert.DoesNotContain("RestartExplorerAsync", fixture.Backend.Requests);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FailedWrite_ReconcilesOnce_WithoutRestartOrRetry(bool authoritative, bool querySucceeds)
    {
        await using var fixture = new Fixture();
        fixture.Backend.Authoritative = authoritative;
        fixture.Backend.QuerySucceeds = querySucceeds;
        // Exercise the same throwing save used by reset; no message-box transport.
        var save = (Task)typeof(SettingsPageViewModel).GetMethod("SaveWin11ContextMenuSettingAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(fixture.Page, [true])!;
        fixture.Backend.Completion.SetException(new TimeoutException("Uncertain write"));
        await Assert.ThrowsAsync<TimeoutException>(() => save);
        Assert.Equal(1, fixture.Backend.Writes);
        Assert.Equal(1, fixture.Backend.Queries);
        Assert.Equal(querySucceeds && authoritative, fixture.Page.Win11ModernContextMenuDisabled);
        Assert.Equal(querySucceeds && authoritative, fixture.Settings.Current.Win11ModernContextMenuDisabled);
        Assert.False(fixture.Restart.NeedsRestart);
        Assert.False(fixture.Page.IsWin11ContextMenuSettingEnabled); // no ready backend in fixture
    }

    [Fact]
    public async Task ResetAndToggleSaves_AreSerialized_AndRefreshCannotApplyAnOlderSnapshot()
    {
        await using var fixture = new Fixture();
        Task Invoke(string method, params object[] args) => (Task)typeof(SettingsPageViewModel)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(fixture.Page, args)!;
        var enable = Invoke("SaveWin11ContextMenuSettingAsync", true);
        var restore = Invoke("SaveWin11ContextMenuSettingAsync", false);
        var refresh = Invoke("LoadWin11ModernContextMenuSettingAsync");
        Assert.Equal(1, fixture.Backend.Writes);
        Assert.Equal(0, fixture.Backend.Queries);
        fixture.Backend.Completion.SetResult(true);
        await Task.WhenAll(enable, restore, refresh);
        Assert.Equal(new[] { true, false }, fixture.Backend.RequestedStates);
        Assert.Equal(1, fixture.Backend.Queries);
        Assert.False(fixture.Page.Win11ModernContextMenuDisabled);
        Assert.False(fixture.Settings.Current.Win11ModernContextMenuDisabled);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ClassicMenuFrontend-" + Guid.NewGuid().ToString("N"));
        public FrontendSettingsService Settings { get; }
        public SettingsPageViewModel Page { get; }
        public BackendRecorder Backend { get; }
        public ExplorerRestartStateService Restart { get; } = new();
        private readonly ContextMenuWorkspaceService _workspace;
        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Settings = new(null, Path.Combine(_directory, "settings.json"));
            var localization = new LocalizationService(Settings);
            var client = DispatchProxy.Create<IBackendClient, BackendRecorder>();
            Backend = (BackendRecorder)(object)client;
            var manager = DispatchProxy.Create<IBackendServiceManager, BackendRecorder>();
            var trust = new PortablePackageTrustService();
            _workspace = new(client, manager, null!, null!, null!, null!, null!, localization, Settings, null!, trust);
            Page = new(Settings, new FrontendStartupService(client), null!, _workspace, localization,
                new FrontendThemeService(Settings), null!, Restart, trust);
        }
        public async Task WaitForSaved(bool value)
        {
            var gate = (SemaphoreSlim)typeof(SettingsPageViewModel).GetField("_win11SettingGate",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Page)!;
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)));
            try { Assert.Equal(value, Settings.Current.Win11ModernContextMenuDisabled); }
            finally { gate.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            Page.Dispose();
            await _workspace.DisposeAsync();
            Directory.Delete(_directory, true);
        }
    }

    public class BackendRecorder : DispatchProxy
    {
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Authoritative;
        public bool QuerySucceeds = true;
        public List<bool> RequestedStates { get; } = [];
        public int Writes;
        public int Queries;
        public List<string> Requests { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var name = method!.Name;
            if (name.StartsWith("add_") || name.StartsWith("remove_")) return null;
            Requests.Add(name);
            if (name == nameof(IBackendClient.SetWin11ModernContextMenuDisabledAsync))
            { Writes++; RequestedStates.Add((bool)args![0]!); return Completion.Task; }
            if (name == nameof(IBackendClient.GetWin11ModernContextMenuDisabledAsync))
            { Queries++; return QuerySucceeds ? Task.FromResult(Authoritative) : Task.FromException<bool>(new IOException("Unavailable")); }
            if (method.ReturnType == typeof(System.ServiceProcess.ServiceControllerStatus?)) return null;
            if (method.ReturnType == typeof(bool)) return false;
            if (method.ReturnType == typeof(ValueTask)) return ValueTask.CompletedTask;
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            throw new InvalidOperationException(name);
        }
    }
}
