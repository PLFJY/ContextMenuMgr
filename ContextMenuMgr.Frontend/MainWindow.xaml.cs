using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.ViewModels;
using ContextMenuMgr.Frontend.Views.Pages;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Forms = System.Windows.Forms;

namespace ContextMenuMgr.Frontend;

public partial class MainWindow : FluentWindow
{
    private readonly LocalizationService _localization;
    private readonly ShellViewModel _viewModel;
    private readonly FrontendSettingsService _settingsService;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _openMenuItem;
    private readonly Forms.ToolStripMenuItem _exitMenuItem;
    private readonly Icon _trayIcon;
    private bool _allowClose;
    private bool _exitInProgress;
    private bool _hasShownTrayHint;

    public MainWindow(
        ShellViewModel viewModel,
        LocalizationService localization,
        FrontendSettingsService settingsService,
        IServiceProvider serviceProvider)
    {
        _viewModel = viewModel;
        _localization = localization;
        _settingsService = settingsService;

        SystemThemeWatcher.Watch(this);
        InitializeComponent();
        RootNavigation.SetServiceProvider(serviceProvider);
        DataContext = _viewModel;

        ApplyWindowIcon();

        _trayIcon = LoadTrayIcon();
        _openMenuItem = new Forms.ToolStripMenuItem();
        _openMenuItem.Click += (_, _) => ShowFromTray();

        _exitMenuItem = new Forms.ToolStripMenuItem();
        _exitMenuItem.Click += async (_, _) => await ExitFromTrayAsync();

        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.AddRange([_openMenuItem, _exitMenuItem]);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            ContextMenuStrip = trayMenu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();

        ApplyTrayLocalization();
        _localization.LanguageChanged += OnLanguageChanged;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        StateChanged += OnStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var app = (App)System.Windows.Application.Current;
        var isStartupLaunch = app.StartupArguments.Any(static arg =>
            string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "/startup", StringComparison.OrdinalIgnoreCase));
        var startInTray = isStartupLaunch && _settingsService.Current.LaunchMinimized;
        var suppressBootstrapPrompt = isStartupLaunch;

        FileNavigationItem.IsActive = true;
        RootNavigation.Navigate(typeof(FileContextMenuPage));

        if (startInTray)
        {
            HideToTray();
        }

        await _viewModel.InitializeAsync(suppressBootstrapPrompt);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ApplyTrayLocalization();
    }

    private void ApplyTrayLocalization()
    {
        _notifyIcon.Text = _localization.Translate("TrayTooltip");
        _openMenuItem.Text = _localization.Translate("TrayOpen");
        _exitMenuItem.Text = _localization.Translate("TrayExit");
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;

        if (_hasShownTrayHint)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = _localization.Translate("TrayBackgroundInfoTitle");
        _notifyIcon.BalloonTipText = _localization.Translate("TrayBackgroundInfoText");
        _notifyIcon.ShowBalloonTip(2500);
        _hasShownTrayHint = true;
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    public void BringToForeground()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            ShowFromTray();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        ShowInTaskbar = true;

        var previousTopmost = Topmost;
        Topmost = true;
        Activate();
        Topmost = previousTopmost;
        Focus();
    }

    private async Task ExitFromTrayAsync()
    {
        if (_exitInProgress)
        {
            return;
        }

        _exitInProgress = true;
        _exitMenuItem.Enabled = false;
        _openMenuItem.Enabled = false;

        FrontendDebugLog.Info("MainWindow", "Tray exit requested. Closing frontend process.");

        try
        {
            _allowClose = true;
            _notifyIcon.Visible = false;
            ShowInTaskbar = false;
            System.Windows.Application.Current.Shutdown();
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("MainWindow", ex, "ExitFromTrayAsync threw.");
            Process.GetCurrentProcess().Kill();
        }
        finally
        {
            if (!_allowClose)
            {
                _exitInProgress = false;
                _exitMenuItem.Enabled = true;
                _openMenuItem.Enabled = true;
            }
        }
    }

    private void ApplyWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            Icon = BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute));
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
