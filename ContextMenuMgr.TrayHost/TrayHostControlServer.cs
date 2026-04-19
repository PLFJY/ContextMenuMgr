using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.TrayHost;

/// <summary>
/// Represents the tray Host Control Server.
/// </summary>
internal sealed class TrayHostControlServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<TrayHostControlRequest, Task<TrayHostControlResponse>> _handler;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrayHostControlServer"/> class.
    /// </summary>
    public TrayHostControlServer(Func<TrayHostControlRequest, Task<TrayHostControlResponse>> handler)
    {
        _handler = handler;
    }

    /// <summary>
    /// Executes start.
    /// </summary>
    public void Start(CancellationToken cancellationToken)
    {
        if (_loopTask is not null && !_loopTask.IsCompleted)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Releases resources used by the current instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask;
            }
            catch
            {
            }
        }

        _cts?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServerStream();
                await server.WaitForConnectionAsync(cancellationToken);
                _ = ObserveClientTaskAsync(server, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch
            {
                server?.Dispose();
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private async Task ObserveClientTaskAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientAsync(stream, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        await using var ownedStream = stream;
        using var reader = new StreamReader(ownedStream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(ownedStream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        try
        {
            var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            var request = JsonSerializer.Deserialize<TrayHostControlRequest>(line, JsonOptions);
            if (request is null)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new TrayHostControlResponse
                {
                    Success = false,
                    Message = "Invalid tray-host control request."
                }, JsonOptions)).WaitAsync(cancellationToken);
                return;
            }

            var response = await _handler(request);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions)).WaitAsync(cancellationToken);
        }
        catch
        {
        }
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.SetAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        pipeSecurity.SetAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        pipeSecurity.SetAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeConstants.TrayHostControlPipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            pipeSecurity);
    }
}
