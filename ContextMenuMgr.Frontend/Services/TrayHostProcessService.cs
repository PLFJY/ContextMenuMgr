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
    private readonly string _trayHostExecutablePath = Path.Combine(AppContext.BaseDirectory, "ContextMenuManager.TrayHost.exe");

    public bool IsRunning()
        => Process.GetProcessesByName("ContextMenuManager.TrayHost").Any();

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
    {
        try
        {
            using var stream = new NamedPipeClientStream(".", PipeConstants.TrayHostControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await stream.ConnectAsync(500, cancellationToken);

            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new TrayHostControlRequest { Command = TrayHostControlCommand.Exit },
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
