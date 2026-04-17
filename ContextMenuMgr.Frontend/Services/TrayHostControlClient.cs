using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

public static class TrayHostControlClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<bool> TrySendExitAsync(CancellationToken cancellationToken)
        => await TrySendCommandAsync(TrayHostControlCommand.Exit, cancellationToken);

    public static async Task<bool> TryReloadLocalizationAsync(CancellationToken cancellationToken)
        => await TrySendCommandAsync(TrayHostControlCommand.ReloadLocalization, cancellationToken);

    private static async Task<bool> TrySendCommandAsync(TrayHostControlCommand command, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new NamedPipeClientStream(".", PipeConstants.TrayHostControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await stream.ConnectAsync(500, cancellationToken);

            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new TrayHostControlRequest { Command = command },
                JsonOptions)).WaitAsync(cancellationToken);

            var line = await reader.ReadLineAsync().WaitAsync(cancellationToken);
            if (line is null)
            {
                return false;
            }

            var response = JsonSerializer.Deserialize<TrayHostControlResponse>(line, JsonOptions);
            return response?.Success == true;
        }
        catch
        {
            return false;
        }
    }
}
