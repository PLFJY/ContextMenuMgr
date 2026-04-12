using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using System.Threading;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Drawing;
using System.Diagnostics;
using Forms = System.Windows.Forms;

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
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _openMenuItem;
    private Forms.ToolStripMenuItem? _exitMenuItem;
    private Icon? _trayIcon;
    private LocalizationService? _localization;
    private FrontendSettingsService? _settingsService;
    private ContextMenuWorkspaceService? _workspace;
    private ShellViewModel? _shellViewModel;
    private Task? _workspaceInitializationTask;
    private IServiceScope? _windowScope;
    private bool _silentStartupToTray;
    private bool _isExiting;
    private bool _hasShownTrayHint;

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
            _settingsService = _serviceProvider.GetRequiredService<FrontendSettingsService>();
            _localization = _serviceProvider.GetRequiredService<LocalizationService>();
            _workspace = _serviceProvider.GetRequiredService<ContextMenuWorkspaceService>();

            FrontendDebugLog.Configure(_settingsService.Current.LogLevel);
            FrontendDebugLog.StartSession("App startup");
            _localization.ApplyPersistedLanguage();
            _serviceProvider.GetRequiredService<ThemeService>().ApplyPersistedTheme();
            SetupTrayIcon();

            var isStartupLaunch = StartupArguments.Any(static arg =>
                string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "/startup", StringComparison.OrdinalIgnoreCase));
            _silentStartupToTray = isStartupLaunch && _settingsService.Current.AutoStartOnLogin;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (_silentStartupToTray)
            {
                _workspaceInitializationTask = _workspace.InitializeNotificationsOnlyAsync(suppressBootstrapPrompt: true);
            }
            else
            {
                ShowMainWindow();
            }
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
            _isExiting = true;
            _notifyIcon?.Dispose();
            _trayIcon?.Dispose();
            _singleInstanceCts?.Cancel();
            _activateEvent?.Set();
            DisposeMainWindowScope();
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
            _notifyIcon = null;
            _trayIcon = null;
            _openMenuItem = null;
            _exitMenuItem = null;
            _localization = null;
            _settingsService = null;
            _workspace = null;
            _shellViewModel = null;
            _workspaceInitializationTask = null;
            _windowScope = null;
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
                    ShowMainWindow(forceActivate: true);
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

    public void MinimizeToTray(bool showNotification = true)
    {
        if (_isExiting)
        {
            return;
        }

        if (MainWindow is MainWindow window)
        {
            BeginDestroyWindowToTray(window, showNotification);
            return;
        }

        ShowTrayHint(showNotification);
    }

    public void BeginDestroyWindowToTray(MainWindow window, bool showNotification = true)
    {
        if (_isExiting)
        {
            return;
        }

        // Normalize state before destroying the window so the next recreated
        // instance does not visually resume from a minimized shell state.
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Hide();
        window.ShowInTaskbar = false;
        ShowTrayHint(showNotification);

        Dispatcher.BeginInvoke(() =>
        {
            if (_isExiting)
            {
                return;
            }

            if (ReferenceEquals(MainWindow, window))
            {
                MainWindow = null;
            }

            window.AllowCloseToTray();
            if (window.IsLoaded)
            {
                window.Close();
            }
        }, DispatcherPriority.Background);
    }

    private void ShowTrayHint(bool showNotification)
    {
        if (_silentStartupToTray)
        {
            return;
        }

        if (!showNotification || _hasShownTrayHint || _notifyIcon is null || _localization is null)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = _localization.Translate("TrayBackgroundInfoTitle");
        _notifyIcon.BalloonTipText = _localization.Translate("TrayBackgroundInfoText");
        _notifyIcon.ShowBalloonTip(2500);
        _hasShownTrayHint = true;
    }

    private void ShowMainWindow(bool forceActivate = false)
    {
        if (_serviceProvider is null || _isExiting)
        {
            return;
        }

        if (MainWindow is not MainWindow window)
        {
            DisposeMainWindowScope();
            _windowScope = _serviceProvider.CreateScope();
            _shellViewModel = _windowScope.ServiceProvider.GetRequiredService<ShellViewModel>();
            _workspaceInitializationTask = _shellViewModel.InitializeAsync(suppressBootstrapPrompt: false);
            window = _windowScope.ServiceProvider.GetRequiredService<MainWindow>();
            window.Closed += OnMainWindowClosed;
            MainWindow = window;
            window.WindowState = WindowState.Normal;
            window.ShowInTaskbar = true;
            window.Show();
        }

        _silentStartupToTray = false;
        window.ShowInTaskbar = true;
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        if (forceActivate)
        {
            var previousTopmost = window.Topmost;
            window.Topmost = true;
            window.Activate();
            window.Topmost = previousTopmost;
            window.Focus();
        }
        else
        {
            window.Activate();
        }
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.Closed -= OnMainWindowClosed;
        }

        MainWindow = null;
        _workspace?.ReleaseUiState();
        DisposeMainWindowScope();
    }

    private void DisposeMainWindowScope()
    {
        try
        {
            if (_windowScope is IAsyncDisposable asyncDisposable)
            {
                asyncDisposable.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
            }
            else
            {
                _windowScope?.Dispose();
            }
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("App", ex, "Failed to dispose main-window scope.");
        }
        finally
        {
            _shellViewModel = null;
            _windowScope = null;
        }
    }

    private void SetupTrayIcon()
    {
        if (_localization is null || _workspace is null)
        {
            return;
        }

        _trayIcon = LoadTrayIcon();
        _openMenuItem = new Forms.ToolStripMenuItem();
        _openMenuItem.Click += (_, _) => ShowMainWindow(forceActivate: true);

        _exitMenuItem = new Forms.ToolStripMenuItem();
        _exitMenuItem.Click += (_, _) => ExitFromTray();

        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.AddRange([_openMenuItem, _exitMenuItem]);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            ContextMenuStrip = trayMenu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow(forceActivate: true);

        ApplyTrayLocalization();
        _localization.LanguageChanged += (_, _) => ApplyTrayLocalization();
        _workspace.PendingApprovalDetected += OnPendingApprovalDetected;
    }

    private void ApplyTrayLocalization()
    {
        if (_notifyIcon is null || _openMenuItem is null || _exitMenuItem is null || _localization is null)
        {
            return;
        }

        _notifyIcon.Text = _localization.Translate("TrayTooltip");
        _openMenuItem.Text = _localization.Translate("TrayOpen");
        _exitMenuItem.Text = _localization.Translate("TrayExit");
    }

    private void OnPendingApprovalDetected(object? sender, ContextMenuMgr.Contracts.ContextMenuEntry entry)
    {
        if (_notifyIcon is null || _localization is null)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = _localization.Translate("PendingApprovalSystemNotificationTitle");
        _notifyIcon.BalloonTipText = _localization.Format("PendingApprovalSystemNotificationMessage", entry.DisplayName);
        _notifyIcon.ShowBalloonTip(4000);
    }

    private void ExitFromTray()
    {
        try
        {
            _isExiting = true;
            if (MainWindow is MainWindow window)
            {
                window.PrepareForAppShutdown();
            }

            _notifyIcon?.Dispose();
            _notifyIcon = null;
            Shutdown();
            Environment.Exit(0);
        }
        catch
        {
            Process.GetCurrentProcess().Kill();
        }
    }

    private static Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        return File.Exists(iconPath)
            ? new Icon(iconPath)
            : (Icon)SystemIcons.Application.Clone();
    }
}
