using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using System.Security.Principal;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class BackendRequestDispatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-Dispatcher-" + Guid.NewGuid().ToString("N"));
    private readonly BackendRequestDispatcher _dispatcher;
    private readonly FileLogger _logger;
    private readonly ContextMenuStateStore _state;

    public BackendRequestDispatcherTests()
    {
        Directory.CreateDirectory(_root);
        _logger = new FileLogger(Path.Combine(_root, "backend.log"));
        _state = new ContextMenuStateStore(Path.Combine(_root, "state.json"), quarantineDirectory: Path.Combine(_root, "Quarantine"));
        var core = new BackendCore(_logger, _state, new RegistryBackupService(Path.Combine(_root, "DeletedBackups")),
            new BackendProtectionSettingsStore(Path.Combine(_root, "protection.json")), FrontendUserClassesRoot.TryOpen,
            new BackendUserContextResolver(_logger).ResolveLoggedInUsers);
        _dispatcher = core.Dispatcher;
    }

    [Fact]
    public async Task SendTo_UsesAuthenticatedProfileAndReturnsVerifiedMutationOrRemoval()
    {
        var caller = new BackendUserContext("S-1-5-21-1-2-3-1001", "Fixture", _root, _root, _root, null);
        var context = new BackendRequestContext(() => caller, _logger);
        var directory = Directory.CreateDirectory(caller.GetSendToPath());
        var path = Path.Combine(directory.FullName, "fixture.txt");
        await File.WriteAllTextAsync(path, "fixture");
        var query = new PipeRequest { Command = PipeCommand.GetSpecialMenuSnapshot, SpecialKind = SpecialMenuKind.SendTo };

        var response = (await _dispatcher.DispatchAsync(query, context, default)).Response;
        Assert.True(response.Success, response.Message);
        Assert.True(response.SnapshotIsComplete);
        Assert.True(response.ClassicSnapshotIsComplete);
        var item = Assert.Single(response.SpecialItems, candidate => candidate.Path == path);
        var operationId = Guid.NewGuid();
        var update = new PipeRequest { Command = PipeCommand.SetSpecialMenuItemEnabled, SpecialItem = item, Enable = false, ClientOperationId = operationId };

        var updated = (await _dispatcher.DispatchAsync(update, context, default)).Response;
        Assert.True(updated.Success, updated.Message);
        Assert.Equal(operationId, updated.ClientOperationId);
        Assert.False(updated.SpecialItem!.IsEnabled);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));

        File.Delete(path);
        var missing = (await _dispatcher.DispatchAsync(update, context, default)).Response;
        Assert.Equal(operationId, missing.ClientOperationId);
        Assert.False(missing.Success);
        Assert.Equal(PipeErrorCodes.MenuItemRemoved, missing.ErrorCode);
        Assert.Equal(item.Id, Assert.Single(missing.RemovedItemIds));
        Assert.False(File.Exists(path));

        var unauthenticated = (await _dispatcher.DispatchAsync(query, new BackendRequestContext(() => null, _logger), default)).Response;
        Assert.False(unauthenticated.Success);
        Assert.NotEqual(true, unauthenticated.SnapshotIsComplete);
    }

    [Theory]
    [InlineData(PipeCommand.SetEnabled, "The request was missing required data.")]
    [InlineData(PipeCommand.GetEnhanceMenuDefinitions, "The request was missing required data.")]
    [InlineData(PipeCommand.GetEnhanceMenuState, "The request was missing required data.")]
    [InlineData(PipeCommand.SetDocumentIconProvider, "Update the frontend to use reversible document icon repair.")]
    public async Task InvalidOrObsoleteCommands_KeepExistingResponseWithoutResolvingCaller(PipeCommand command, string message)
    {
        var context = new BackendRequestContext(() => throw new InvalidOperationException("Unexpected identity lookup."), _logger);
        var result = await _dispatcher.DispatchAsync(new PipeRequest { Command = command }, context, default);
        Assert.Null(result.Error);
        Assert.False(result.Response.Success);
        Assert.Equal(message, result.Response.Message);
    }

    [Fact]
    public async Task DocumentIconRepair_ReportsStructuredAccessFailureAtDispatchBoundary()
    {
        var denial = new UnauthorizedAccessException("The user hive could not be opened.");
        var context = new BackendRequestContext(() => throw denial, _logger);
        var request = new PipeRequest { Command = PipeCommand.SetDocumentIconRepair, DocumentIconRepairEnabled = true, ClientOperationId = Guid.NewGuid() };
        var result = await _dispatcher.DispatchAsync(request, context, default);

        Assert.Same(denial, result.Error);
        Assert.False(result.Response.Success);
        Assert.Equal(request.ClientOperationId, result.Response.ClientOperationId);
        Assert.Equal(PipeErrorCodes.DocumentIconRepairAccessDenied, result.Response.ErrorCode);
    }

    [Fact]
    public async Task EmptySceneQuery_DistinguishesMissingUserFromCompleteEmptyResult()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = new BackendUserContext(identity.User!, "Fixture", _root, _root, _root, null);
        var query = new PipeRequest { Command = PipeCommand.GetSceneSnapshot, SceneKind = ContextMenuSceneKind.CustomExtension };
        var partial = await _dispatcher.DispatchAsync(query, new BackendRequestContext(() => null, _logger), default);
        var complete = await _dispatcher.DispatchAsync(query, new BackendRequestContext(() => user, _logger), default);

        Assert.True(partial.Response.Success);
        Assert.Empty(partial.Response.Items);
        Assert.False(partial.Response.ClassicSnapshotIsComplete);
        Assert.False(partial.Response.SnapshotIsComplete);
        Assert.True(complete.Response.Success);
        Assert.Empty(complete.Response.Items);
        Assert.True(complete.Response.ClassicSnapshotIsComplete);
        Assert.True(complete.Response.SnapshotIsComplete);
    }

    [Fact]
    public async Task CoreStateReset_SerializesWithAnOpenWithWrite()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var releaseWrite = new ManualResetEventSlim();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(() => _state.WithOpenWithAsync(state =>
        {
            state.Users["S-1-5-21-1-2-3-1001"] = new();
            writeEntered.SetResult();
            releaseWrite.Wait(timeout.Token);
            return true;
        }, true, timeout.Token));
        await writeEntered.Task.WaitAsync(timeout.Token);

        var context = new BackendRequestContext(() => null, _logger);
        var reset = _dispatcher.DispatchAsync(new PipeRequest { Command = PipeCommand.ResetStateDatabase }, context, timeout.Token);
        var resetWaited = !reset.IsCompleted;
        releaseWrite.Set();
        Assert.True(await writer.WaitAsync(timeout.Token));
        Assert.True((await reset.WaitAsync(timeout.Token)).Response.Success);
        Assert.True(resetWaited);
        Assert.Empty(await _state.WithOpenWithAsync(state => state.Users.Keys.ToArray(), false, timeout.Token));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
