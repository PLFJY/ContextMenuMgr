using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class FrontendControlClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SynchronousCallerWithNonPumpingSynchronizationContext_DoesNotDeadlock()
    {
        var pipeName = CreatePipeName();
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = ServeOneRequestAsync(pipeName, requestReceived, sendResponse.Task, serverCts.Token);
        Exception? clientException = null;
        var clientResult = false;

        var clientThread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                clientResult = FrontendControlClient
                    .TrySendAsync(
                        CreateRequest(),
                        pipeName,
                        TimeSpan.FromSeconds(2),
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                clientException = ex;
            }
        })
        {
            IsBackground = true
        };

        clientThread.Start();
        await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        sendResponse.SetResult();

        Assert.True(
            clientThread.Join(TimeSpan.FromSeconds(3)),
            "The synchronous frontend startup caller remained blocked on an unpumped SynchronizationContext.");
        Assert.Null(clientException);
        Assert.True(clientResult);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task DelayedServerReadiness_RetriesAndDeliversActivation()
    {
        var pipeName = CreatePipeName();
        var firstAttemptFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationTask = FrontendControlClient.TrySendWithStartupRetryAsync(
            CreateRequest(),
            pipeName,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None,
            (attempt, succeeded) =>
            {
                if (attempt == 1 && !succeeded)
                {
                    firstAttemptFailed.TrySetResult();
                }
            });

        await firstAttemptFailed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeOneRequestAsync(pipeName, requestReceived, Task.CompletedTask, CancellationToken.None);

        Assert.True(await activationTask.WaitAsync(TimeSpan.FromSeconds(3)));
        await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ConnectedServerWithoutResponse_ReturnsFalseWithinOverallTimeout()
    {
        var pipeName = CreatePipeName();
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverRespond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var serverTask = ServeOneRequestAsync(pipeName, requestReceived, neverRespond.Task, serverCts.Token);
        var stopwatch = Stopwatch.StartNew();

        var result = await FrontendControlClient.TrySendAsync(
            CreateRequest(),
            pipeName,
            TimeSpan.FromMilliseconds(300),
            CancellationToken.None);

        Assert.False(result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        serverCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => serverTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ConcurrentSecondaryActivations_AllFinishAfterPrimaryBecomesReady()
    {
        const int clientCount = 8;
        var pipeName = CreatePipeName();
        var firstAttemptsFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedFirstAttemptCount = 0;
        var activationTasks = Enumerable.Range(0, clientCount)
            .Select(_ => FrontendControlClient.TrySendWithStartupRetryAsync(
                CreateRequest(),
                pipeName,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromMilliseconds(150),
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None,
                (attempt, succeeded) =>
                {
                    if (attempt == 1
                        && !succeeded
                        && Interlocked.Increment(ref failedFirstAttemptCount) == clientCount)
                    {
                        firstAttemptsFailed.TrySetResult();
                    }
                }))
            .ToArray();

        await firstAttemptsFailed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var receivedRequests = 0;
        var serverTasks = Enumerable.Range(0, clientCount)
            .Select(_ => ServeOneRequestAsync(
                pipeName,
                requestReceived: null,
                Task.CompletedTask,
                CancellationToken.None,
                () => Interlocked.Increment(ref receivedRequests)))
            .ToArray();

        var results = await Task.WhenAll(activationTasks).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, Assert.True);
        await Task.WhenAll(serverTasks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(clientCount, receivedRequests);
    }

    private static FrontendControlRequest CreateRequest()
        => new() { Command = FrontendControlCommand.ShowMainWindow };

    private static string CreatePipeName()
        => $"ContextMenuMgr.Tests.FrontendControl.{Guid.NewGuid():N}";

    private static async Task ServeOneRequestAsync(
        string pipeName,
        TaskCompletionSource? requestReceived,
        Task responseGate,
        CancellationToken cancellationToken,
        Action? onRequestReceived = null)
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(cancellationToken);

        using var reader = new StreamReader(
            server,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        using var writer = new StreamWriter(
            server,
            new UTF8Encoding(false),
            leaveOpen: true)
        {
            AutoFlush = true
        };

        var line = await reader.ReadLineAsync(cancellationToken);
        Assert.NotNull(JsonSerializer.Deserialize<FrontendControlRequest>(line!, JsonOptions));
        onRequestReceived?.Invoke();
        requestReceived?.TrySetResult();
        await responseGate.WaitAsync(cancellationToken);
        await writer.WriteLineAsync(
            JsonSerializer.Serialize(
                new FrontendControlResponse { Success = true },
                JsonOptions));
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }
}
