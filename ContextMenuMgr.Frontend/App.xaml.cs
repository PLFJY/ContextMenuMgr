using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using ContextMenuMgr.Frontend.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace ContextMenuMgr.Frontend;

/// <summary>
/// Represents the app.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\PLFJY.ContextMenuManagerPlus.SingleInstance";
    private const string ProtocolScheme = "contextmenumgrplus";
    private static readonly TimeSpan CrashLogRetention = TimeSpan.FromDays(7);
    private static readonly string LogFilePath = RuntimePaths.FrontendCrashLogPath;

    private ServiceProvider? _serviceProvider;
    private FrontendControlServer? _controlServer;
    private CancellationTokenSource? _controlServerCts;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private MainWindow? _mainWindow;
    private ShellViewModel? _shellViewModel;
    private LocalizationService? _localization;
    private FrontendNavigationState? _navigationState;
    private TrayHostProcessService? _trayHostProcessService;
    private FrontendSettingsService? _frontendSettingsService;
    private bool _isShuttingDown;

    /// <summary>
    /// Gets or sets the startup Arguments.
    /// </summary>
    public string[] StartupArguments { get; private set; } = [];

    public T? TryGetService<T>() where T : class => _serviceProvider?.GetService<T>();

    protected override void OnStartup(StartupEventArgs e)
    {
        PruneOldCrashLogs();
        RegisterGlobalExceptionHandlers();
        StartupArguments = e.Args ?? [];

        var initialRequest = ParseFrontendControlRequest(StartupArguments);
        try
        {
            if (!InitializeSingleInstance(initialRequest))
            {
                Shutdown(0);
                return;
            }

            _serviceProvider = BuildServiceProvider();
            var settingsService = _serviceProvider.GetRequiredService<FrontendSettingsService>();
            _frontendSettingsService = settingsService;
            _localization = _serviceProvider.GetRequiredService<LocalizationService>();
            _navigationState = _serviceProvider.GetRequiredService<FrontendNavigationState>();
            _trayHostProcessService = _serviceProvider.GetRequiredService<TrayHostProcessService>();

            FrontendDebugLog.Configure(settingsService.Current.LogLevel);
            FrontendDebugLog.StartSession("App startup");
            _localization.ApplyPersistedLanguage();

            _controlServerCts = new CancellationTokenSource();
            _controlServer = new FrontendControlServer(HandleFrontendControlRequestAsync);
            _controlServer.Start(_controlServerCts.Token);

            ShutdownMode = ShutdownMode.OnMainWindowClose;
            ShowMainWindow(initialRequest, forceActivate: true);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("App", ex, "Fatal error while bootstrapping frontend.");
            HandleFatalException("Startup", ex);
            Shutdown(-1);
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;
        try
        {
            _controlServerCts?.Cancel();
            _controlServer?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
            _shellViewModel?.Dispose();
            _serviceProvider?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("App", ex, "Failed to dispose frontend resources during shutdown.");
        }
        finally
        {
            _controlServerCts?.Dispose();
            _controlServerCts = null;
            _controlServer = null;
            _shellViewModel = null;
            _mainWindow = null;
            _localization = null;
            _navigationState = null;
            _trayHostProcessService = null;
            _frontendSettingsService = null;
            _serviceProvider = null;
            if (_ownsSingleInstanceMutex)
            {
                _singleInstanceMutex?.ReleaseMutex();
            }

            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            _ownsSingleInstanceMutex = false;
        }

        base.OnExit(e);
    }

    private bool InitializeSingleInstance(FrontendControlRequest initialRequest)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (createdNew)
        {
            _ownsSingleInstanceMutex = true;
            return true;
        }

        var attemptCount = 0;
        if (FrontendControlClient
            .TrySendWithStartupRetryAsync(
                initialRequest,
                CancellationToken.None,
                (attempt, _) => attemptCount = attempt)
            .GetAwaiter()
            .GetResult())
        {
            FrontendDebugLog.Operation(
                "App",
                $"Secondary frontend activation succeeded. PID={Environment.ProcessId}, Command={initialRequest.Command}, Attempts={attemptCount}.");
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }

        try
        {
            if (_singleInstanceMutex.WaitOne(0))
            {
                _ownsSingleInstanceMutex = true;
                FrontendDebugLog.Operation(
                    "App",
                    $"Acquired released single-instance mutex after activation retry. PID={Environment.ProcessId}, Attempts={attemptCount}.");
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            _ownsSingleInstanceMutex = true;
            FrontendDebugLog.Operation(
                "App",
                $"Acquired abandoned single-instance mutex after activation retry. PID={Environment.ProcessId}, Attempts={attemptCount}.");
            return true;
        }

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        FrontendDebugLog.Warning(
            "App",
            $"Secondary frontend activation startup grace exhausted. PID={Environment.ProcessId}, Attempts={attemptCount}; existing mutex remains owned, so no stale-process termination was attempted.");
        return false;
    }

    private static FrontendControlRequest ParseFrontendControlRequest(string[] args)
    {
        var protocolRequest = TryParseProtocolActivationRequest(args);
        if (protocolRequest is not null)
        {
            return protocolRequest;
        }

        var focusItemId = GetArgumentValue(args, "--focus-item");
        if (args.Any(static arg => string.Equals(arg, "--open-approvals", StringComparison.OrdinalIgnoreCase)))
        {
            return new FrontendControlRequest
            {
                Command = FrontendControlCommand.OpenApprovals,
                FocusItemId = focusItemId
            };
        }

        return new FrontendControlRequest
        {
            Command = FrontendControlCommand.ShowMainWindow,
            FocusItemId = focusItemId
        };
    }

    private static string? GetArgumentValue(IReadOnlyList<string> args, string argumentName)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], argumentName, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static FrontendControlRequest? TryParseProtocolActivationRequest(IReadOnlyList<string> args)
    {
        foreach (var argument in args)
        {
            if (string.IsNullOrWhiteSpace(argument)
                || !Uri.TryCreate(argument, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, ProtocolScheme, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var command = string.IsNullOrWhiteSpace(uri.Host)
                ? uri.AbsolutePath.Trim('/')
                : uri.Host;

            if (string.Equals(command, "open-approvals", StringComparison.OrdinalIgnoreCase))
            {
                return new FrontendControlRequest
                {
                    Command = FrontendControlCommand.OpenApprovals,
                    FocusItemId = GetQueryValue(uri.Query, "itemId")
                };
            }
        }

        return null;
    }

    private static string? GetQueryValue(string query, string name)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var trimmedQuery = query[0] == '?' ? query[1..] : query;
        foreach (var pair in trimmedQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace("+", " "));
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return parts.Length > 1
                ? Uri.UnescapeDataString(parts[1].Replace("+", " "))
                : string.Empty;
        }

        return null;
    }

    private async Task<FrontendControlResponse> HandleFrontendControlRequestAsync(FrontendControlRequest request)
    {
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                switch (request.Command)
                {
                    case FrontendControlCommand.ShowMainWindow:
                        ShowMainWindow(request, forceActivate: true);
                        break;
                    case FrontendControlCommand.OpenApprovals:
                        ShowMainWindow(request, forceActivate: true);
                        break;
                    case FrontendControlCommand.Shutdown:
                        ShutdownFrontend();
                        break;
                }
            });

            return new FrontendControlResponse
            {
                Success = true,
                Message = "Frontend command applied."
            };
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("App", ex, $"Failed to handle frontend control command {request.Command}.");
            return new FrontendControlResponse
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    private void ShowMainWindow(FrontendControlRequest request, bool forceActivate)
    {
        if (_serviceProvider is null || _isShuttingDown)
        {
            return;
        }

        if (_mainWindow is null)
        {
            _mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            _mainWindow.Closed += OnMainWindowClosed;
            _mainWindow.Closing += OnMainWindowClosing;
            _serviceProvider.GetRequiredService<FrontendThemeService>().Initialize(_mainWindow);
            _mainWindow.Show();
            _mainWindow.ShowInTaskbar = true;

            _shellViewModel = _mainWindow.DataContext as ShellViewModel;
            _ = _shellViewModel?.InitializeAsync(suppressBootstrapPrompt: false);
        }

        if (request.Command == FrontendControlCommand.OpenApprovals)
        {
            _navigationState?.RequestApprovals(request.FocusItemId);
            _mainWindow.NavigateTo(typeof(ApprovalsPage));
        }
        else
        {
            _navigationState?.ClearFocusItem();
            _mainWindow.NavigateTo(typeof(FileContextMenuPage));
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            SystemCommands.RestoreWindow(_mainWindow);
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (forceActivate)
        {
            _mainWindow.BringToForeground();
        }
        else
        {
            _mainWindow.Activate();
        }
    }

    private void ShutdownFrontend()
    {
        _isShuttingDown = true;
        Shutdown();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (_mainWindow is not null)
        {
            _mainWindow.Closed -= OnMainWindowClosed;
            _mainWindow.Closing -= OnMainWindowClosing;
        }

        _mainWindow = null;
        _shellViewModel = null;
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isShuttingDown || _frontendSettingsService?.Current.KeepBackgroundAfterClose != false)
        {
            return;
        }

        TryShutdownBackgroundRuntimeOnClose();
    }

    private void TryShutdownBackgroundRuntimeOnClose()
    {
        try
        {
            var shutdownTask = Task.Run(async () =>
            {
                if (_serviceProvider?.GetService<IBackendClient>() is { } backendClient)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                        await backendClient.RequestShutdownAsync(cts.Token);
                    }
                    catch
                    {
                    }
                }

                if (_trayHostProcessService is not null)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                        await _trayHostProcessService.RequestExitAsync(cts.Token);
                    }
                    catch
                    {
                    }
                }
            });

            shutdownTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HandleFatalException("DispatcherUnhandledException", e.Exception);
        e.Handled = true;
        Shutdown(-1);
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            HandleFatalException("AppDomainUnhandledException", exception);
            return;
        }

        HandleFatalMessage("AppDomainUnhandledException", e.ExceptionObject?.ToString() ?? "Unknown fatal error.");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        HandleFatalException("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void HandleFatalException(string source, Exception exception)
    {
        var builder = new StringBuilder()
            .AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}")
            .AppendLine(exception.ToString())
            .AppendLine();

        WriteLog(builder.ToString());

        MessageBox.Show(
            $"应用发生未处理异常，详细信息已写入：\n{LogFilePath}\n\n{exception.Message}",
            "Context Menu Manager Plus",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void HandleFatalMessage(string source, string message)
    {
        var text = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}";
        WriteLog(text);
    }

    private static void WriteLog(string text)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(LogFilePath, text, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void PruneOldCrashLogs()
    {
        try
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var cutoff = DateTimeOffset.Now.Subtract(CrashLogRetention);
            foreach (var file in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var lastWriteTime = File.GetLastWriteTimeUtc(file);
                    if (lastWriteTime < cutoff.UtcDateTime)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}
