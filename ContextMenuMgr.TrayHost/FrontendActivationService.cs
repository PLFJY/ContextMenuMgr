using System.Diagnostics;
using System.IO;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.TrayHost;

/// <summary>
/// Represents the frontend Activation Service.
/// </summary>
internal sealed class FrontendActivationService
{
    private readonly string _frontendExePath;
    private readonly Lock _activationLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FrontendActivationService"/> class.
    /// </summary>
    public FrontendActivationService(string baseDirectory)
    {
        _frontendExePath = Path.Combine(baseDirectory, "ContextMenuManagerPlus.exe");
    }

    /// <summary>
    /// Attempts to show Main Window.
    /// </summary>
    public bool TryShowMainWindow()
        => TryOpenFrontend(
            new FrontendControlRequest { Command = FrontendControlCommand.ShowMainWindow },
            "--show-main");

    /// <summary>
    /// Attempts to open Approvals.
    /// </summary>
    public bool TryOpenApprovals(string? focusItemId)
        => TryOpenFrontend(
            new FrontendControlRequest
            {
                Command = FrontendControlCommand.OpenApprovals,
                FocusItemId = focusItemId
            },
            BuildArguments("--open-approvals", focusItemId));

    /// <summary>
    /// Attempts to shutdown Frontend.
    /// </summary>
    public bool TryShutdownFrontend()
        => TrySendFrontendControlRequest(
            new FrontendControlRequest
            {
                Command = FrontendControlCommand.Shutdown
            });

    private bool TryOpenFrontend(FrontendControlRequest request, string startupArguments)
    {
        lock (_activationLock)
        {
            if (IsFrontendRunningInCurrentSession())
            {
                if (TrySendFrontendControlRequestWithStartupRetry(request))
                {
                    return true;
                }

                if (IsFrontendRunningInCurrentSession())
                {
                    return false;
                }
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
        => FrontendControlPipeClient
            .TrySendAsync(request, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private static bool TrySendFrontendControlRequestWithStartupRetry(FrontendControlRequest request)
        => FrontendControlPipeClient
            .TrySendWithStartupRetryAsync(request, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private static bool IsFrontendRunningInCurrentSession()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var currentSessionId = currentProcess.SessionId;
        foreach (var process in Process.GetProcessesByName("ContextMenuManagerPlus"))
        {
            try
            {
                if (process.SessionId == currentSessionId)
                {
                    return true;
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }
}
