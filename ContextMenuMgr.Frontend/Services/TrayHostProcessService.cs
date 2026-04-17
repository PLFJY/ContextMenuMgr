using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

public sealed class TrayHostProcessService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _trayHostExecutablePath = Path.Combine(AppContext.BaseDirectory, "ContextMenuManagerPlus.TrayHost.exe");

    public bool IsRunning()
        => Process.GetProcessesByName("ContextMenuManagerPlus.TrayHost").Any();

    public bool EnsureRunning()
    {
        if (IsRunning())
        {
            return true;
        }

        if (!File.Exists(_trayHostExecutablePath))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _trayHostExecutablePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(_trayHostExecutablePath) ?? AppContext.BaseDirectory
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> RequestExitAsync(CancellationToken cancellationToken)
        => await SendCommandAsync(TrayHostControlCommand.Exit, cancellationToken);

    public async Task<bool> RequestReloadLocalizationAsync(CancellationToken cancellationToken)
        => await SendCommandAsync(TrayHostControlCommand.ReloadLocalization, cancellationToken);

    private static async Task<bool> SendCommandAsync(TrayHostControlCommand command, CancellationToken cancellationToken)
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
