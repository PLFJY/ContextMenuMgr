using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.TrayHost;

internal sealed class TrayBackendPipeClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public event EventHandler<BackendNotification>? NotificationReceived;

    public event EventHandler? BackendUnavailable;

    public void Start()
    {
        lock (_sync)
        {
            if (_loopTask is not null && !_loopTask.IsCompleted)
            {
                return;
            }

            _loopCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => NotificationLoopAsync(_loopCts.Token), CancellationToken.None);
        }
    }

    public async Task RequestBackendShutdownAsync(CancellationToken cancellationToken)
    {
        using var stream = new NamedPipeClientStream(".", PipeConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync(2000, cancellationToken);
        stream.ReadMode = PipeTransmissionMode.Byte;

        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        var envelope = new PipeEnvelope
        {
            MessageType = PipeMessageType.Request,
            CorrelationId = Guid.NewGuid(),
            Request = new PipeRequest { Command = PipeCommand.RequestShutdown }
        };

        await writer.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonOptions)).WaitAsync(cancellationToken);
        _ = await reader.ReadLineAsync().WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_sync)
        {
            cts = _loopCts;
            task = _loopTask;
            _loopCts = null;
            _loopTask = null;
        }

        cts?.Cancel();
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch
            {
            }
        }

        cts?.Dispose();
    }

    private async Task NotificationLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new NamedPipeClientStream(".", PipeConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await stream.ConnectAsync(5000, cancellationToken);
            stream.ReadMode = PipeTransmissionMode.Byte;

            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            var subscriptionEnvelope = new PipeEnvelope
            {
                MessageType = PipeMessageType.Request,
                CorrelationId = Guid.NewGuid(),
                Request = new PipeRequest { Command = PipeCommand.SubscribeNotifications }
            };

            await writer.WriteLineAsync(JsonSerializer.Serialize(subscriptionEnvelope, JsonOptions)).WaitAsync(cancellationToken);
            var ackLine = await reader.ReadLineAsync().WaitAsync(cancellationToken);
            if (ackLine is null)
            {
                throw new InvalidOperationException("The backend pipe closed before acknowledging the notification subscription.");
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                var envelope = JsonSerializer.Deserialize<PipeEnvelope>(line, JsonOptions);
                if (envelope?.MessageType == PipeMessageType.Notification && envelope.Notification is not null)
                {
                    NotificationReceived?.Invoke(this, envelope.Notification);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            BackendUnavailable?.Invoke(this, EventArgs.Empty);
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            BackendUnavailable?.Invoke(this, EventArgs.Empty);
        }
    }
}
