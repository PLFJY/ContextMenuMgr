using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using System.Threading;
using ContextMenuMgr.Frontend.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ContextMenuMgr.Frontend;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Global\PLFJY.ContextMenuManager.SingleInstance";
    private const string ActivateEventName = @"Global\PLFJY.ContextMenuManager.Activate";
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ContextMenuMgr",
        "Logs",
        "frontend-crash.log");

    private ServiceProvider? _serviceProvider;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _singleInstanceCts;
    private Task? _singleInstanceListenerTask;

    public string[] StartupArguments { get; private set; } = [];

    public T? TryGetService<T>() where T : class
    {
        return _serviceProvider?.GetService<T>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterGlobalExceptionHandlers();
        StartupArguments = e.Args ?? [];

        try
        {
            if (!InitializeSingleInstance())
            {
                Shutdown(0);
                return;
            }

            _serviceProvider = BuildServiceProvider();
            var settings = _serviceProvider.GetRequiredService<FrontendSettingsService>();
            FrontendDebugLog.Configure(settings.Current.LogLevel);
            FrontendDebugLog.StartSession("App startup");
            _serviceProvider.GetRequiredService<LocalizationService>().ApplyPersistedLanguage();
            _serviceProvider.GetRequiredService<ThemeService>().ApplyPersistedTheme();
            var window = _serviceProvider.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("App", ex, "Fatal error while bootstrapping DI/application services.");
            HandleFatalException("Startup", ex);
            Shutdown(-1);
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceCts?.Cancel();
            _activateEvent?.Set();
            _singleInstanceListenerTask?.Wait(TimeSpan.FromSeconds(1));
            _serviceProvider?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("App", ex, "Failed to dispose service provider during shutdown.");
        }
        finally
        {
            _singleInstanceCts?.Dispose();
            _singleInstanceCts = null;
            _activateEvent?.Dispose();
            _activateEvent = null;
            if (_ownsSingleInstanceMutex)
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            _ownsSingleInstanceMutex = false;
            _serviceProvider = null;
        }

        base.OnExit(e);
    }

    private bool InitializeSingleInstance()
    {
        var createdNew = false;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out createdNew);
        if (!createdNew)
        {
            try
            {
                using var activateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
                activateEvent.Set();
            }
            catch
            {
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }

        _ownsSingleInstanceMutex = true;
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _singleInstanceCts = new CancellationTokenSource();
        _singleInstanceListenerTask = Task.Run(() => ListenForActivationRequestsAsync(_singleInstanceCts.Token));
        return true;
    }

    private async Task ListenForActivationRequestsAsync(CancellationToken cancellationToken)
    {
        if (_activateEvent is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _activateEvent.WaitOne();
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (MainWindow is MainWindow window)
                    {
                        window.BringToForeground();
                    }
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
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

        System.Windows.MessageBox.Show(
            $"应用发生未处理异常，详细信息已写入：\n{LogFilePath}\n\n{exception.Message}",
            "Context Menu Manager",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
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
}
