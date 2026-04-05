using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ContextMenuMgr.Frontend.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ContextMenuMgr.Frontend;

public partial class App : System.Windows.Application
{
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ContextMenuMgr",
        "Logs",
        "frontend-crash.log");

    private ServiceProvider? _serviceProvider;

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
            _serviceProvider = BuildServiceProvider();
            var settings = _serviceProvider.GetRequiredService<FrontendSettingsService>();
            FrontendDebugLog.Configure(settings.Current.LogLevel);
            FrontendDebugLog.StartSession("App startup");
            _serviceProvider.GetRequiredService<LocalizationService>().ApplyPersistedLanguage();
            _serviceProvider.GetRequiredService<ThemeService>().ApplyPersistedTheme();
            var window = _serviceProvider.GetRequiredService<MainWindow>();
            if (settings.Current.LaunchMinimized)
            {
                window.WindowState = WindowState.Minimized;
            }

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
            _serviceProvider?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("App", ex, "Failed to dispose service provider during shutdown.");
        }
        finally
        {
            _serviceProvider = null;
        }

        base.OnExit(e);
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
