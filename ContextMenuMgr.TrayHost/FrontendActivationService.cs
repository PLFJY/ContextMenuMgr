using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.TrayHost;

internal sealed class FrontendActivationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _frontendExePath;

    public FrontendActivationService(string baseDirectory)
    {
        _frontendExePath = Path.Combine(baseDirectory, "ContextMenuManagerPlus.exe");
    }

    public bool TryShowMainWindow()
        => TryOpenFrontend(
            new FrontendControlRequest { Command = FrontendControlCommand.ShowMainWindow },
            "--show-main");

    public bool TryOpenApprovals(string? focusItemId)
        => TryOpenFrontend(
            new FrontendControlRequest
            {
                Command = FrontendControlCommand.OpenApprovals,
                FocusItemId = focusItemId
            },
            BuildArguments("--open-approvals", focusItemId));

    public bool TryShutdownFrontend()
        => TrySendFrontendControlRequest(
            new FrontendControlRequest
            {
                Command = FrontendControlCommand.Shutdown
            });

    private bool TryOpenFrontend(FrontendControlRequest request, string startupArguments)
    {
        if (TrySendFrontendControlRequest(request))
        {
            return true;
        }

        if (!File.Exists(_frontendExePath))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _frontendExePath,
                Arguments = startupArguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(_frontendExePath) ?? AppContext.BaseDirectory
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildArguments(string command, string? focusItemId)
    {
        if (string.IsNullOrWhiteSpace(focusItemId))
        {
            return command;
        }

        return $"{command} --focus-item \"{focusItemId}\"";
    }

    private static bool TrySendFrontendControlRequest(FrontendControlRequest request)
    {
        try
        {
            using var stream = new NamedPipeClientStream(".", PipeConstants.FrontendControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            stream.Connect(500);

            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            writer.WriteLine(JsonSerializer.Serialize(request, JsonOptions));
            var line = reader.ReadLine();
            if (line is null)
            {
                return false;
            }

            var response = JsonSerializer.Deserialize<FrontendControlResponse>(line, JsonOptions);
            return response?.Success == true;
        }
        catch
        {
            return false;
        }
    }
}
