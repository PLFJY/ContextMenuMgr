using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using ContextMenuMgr.Contracts;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace ContextMenuMgr.TrayHost;

internal sealed class TrayHostRunner : IDisposable
{
    private const string TrayMutexName = @"Local\PLFJY.ContextMenuManager.TrayHost";

    private readonly TrayBackendPipeClient _backendPipeClient;
    private readonly FrontendActivationService _frontendActivationService;
    private readonly TrayHostLogger _logger;
    private readonly TrayLocalizationService _localization;
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private Application? _application;
    private TaskbarIcon? _taskbarIcon;
    private NativeTrayMenu? _nativeTrayMenu;
    private Icon? _trayIcon;
    private TrayHostControlServer? _controlServer;
    private CancellationTokenSource? _controlServerCts;
    private string? _pendingApprovalItemId;
    private bool _isClosing;

    public TrayHostRunner(
        TrayBackendPipeClient backendPipeClient,
        FrontendActivationService frontendActivationService,
        TrayHostLogger logger)
    {
        _backendPipeClient = backendPipeClient;
        _frontendActivationService = frontendActivationService;
        _logger = logger;
        _localization = new TrayLocalizationService();
    }

    public int Run()
    {
        if (!AcquireSingleInstance())
        {
            return 0;
        }

        try
        {
            _application = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            _nativeTrayMenu = new NativeTrayMenu(
                ShowMainWindow,
                RequestBackendShutdown,
                _localization.Translate("Tray.ShowMainWindow"),
                _localization.Translate("Tray.ExitFull"));

            _backendPipeClient.NotificationReceived += OnNotificationReceived;
            _backendPipeClient.BackendUnavailable += OnBackendUnavailable;

            _controlServerCts = new CancellationTokenSource();
            _controlServer = new TrayHostControlServer(HandleTrayControlRequestAsync);
            _controlServer.Start(_controlServerCts.Token);

            CreateTrayIcon();
            _backendPipeClient.Start();

            return _application.Run();
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        _backendPipeClient.NotificationReceived -= OnNotificationReceived;
        _backendPipeClient.BackendUnavailable -= OnBackendUnavailable;
        _backendPipeClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _controlServerCts?.Cancel();
        _controlServer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _controlServerCts?.Dispose();
        _controlServerCts = null;
        _controlServer = null;

        if (_taskbarIcon is not null)
        {
            _taskbarIcon.TrayLeftMouseUp -= OnTrayLeftMouseUp;
            _taskbarIcon.TrayLeftMouseDoubleClick -= OnTrayLeftMouseDoubleClick;
            _taskbarIcon.TrayRightMouseUp -= OnTrayRightMouseUp;
            _taskbarIcon.TrayBalloonTipClicked -= OnTrayBalloonTipClicked;
            _taskbarIcon.Dispose();
            _taskbarIcon = null;
        }

        _nativeTrayMenu?.Dispose();
        _nativeTrayMenu = null;

        _trayIcon?.Dispose();
        _trayIcon = null;
        _application = null;

        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _ownsMutex = false;
    }

    private bool AcquireSingleInstance()
    {
        _singleInstanceMutex = new Mutex(true, TrayMutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            return false;
        }

        return true;
    }

    private void CreateTrayIcon()
    {
        _trayIcon = LoadTrayIcon();
        _taskbarIcon = new TaskbarIcon
        {
            Icon = _trayIcon,
            ToolTipText = _localization.Translate("Tray.Tooltip"),
            NoLeftClickDelay = true
        };

        _taskbarIcon.TrayLeftMouseUp += OnTrayLeftMouseUp;
        _taskbarIcon.TrayLeftMouseDoubleClick += OnTrayLeftMouseDoubleClick;
        _taskbarIcon.TrayRightMouseUp += OnTrayRightMouseUp;
        _taskbarIcon.TrayBalloonTipClicked += OnTrayBalloonTipClicked;
        _taskbarIcon.ForceCreate();
    }

    private void OnTrayLeftMouseUp(object? sender, RoutedEventArgs e) => ShowMainWindow();

    private void OnTrayLeftMouseDoubleClick(object? sender, RoutedEventArgs e) => ShowMainWindow();

    private void OnTrayRightMouseUp(object? sender, RoutedEventArgs e)
    {
        _nativeTrayMenu?.ShowAtCursor();
    }

    private void OnTrayBalloonTipClicked(object? sender, RoutedEventArgs e) => OpenApprovals();

    private void ShowMainWindow()
    {
        _frontendActivationService.TryShowMainWindow();
    }

    private void OpenApprovals()
    {
        _frontendActivationService.TryOpenApprovals(_pendingApprovalItemId);
    }

    private void RequestBackendShutdown()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _backendPipeClient.RequestBackendShutdownAsync(CancellationToken.None);
                RequestClose();
            }
            catch (Exception ex)
            {
                await _logger.LogAsync($"Failed to request backend shutdown from tray host: {ex.Message}");
                RequestClose();
            }
        });
    }

    private void OnNotificationReceived(object? sender, BackendNotification notification)
    {
        if (notification.Kind == PipeNotificationKind.ServiceStopping)
        {
            RequestClose();
            return;
        }

        if (notification.Kind != PipeNotificationKind.ItemDetected || notification.Item is null)
        {
            return;
        }

        _pendingApprovalItemId = notification.Item.Id;

        if (_application is null || _taskbarIcon is null)
        {
            return;
        }

        _ = _application.Dispatcher.InvokeAsync(() =>
        {
            _taskbarIcon?.ShowNotification(
                _localization.Translate("Tray.PendingApprovalTitle"),
                _localization.Format("Tray.PendingApprovalMessage", notification.Item.DisplayName),
                NotificationIcon.Info);
        });
    }

    private void OnBackendUnavailable(object? sender, EventArgs e)
    {
        _ = _logger.LogAsync("Backend became unavailable while tray host stays alive.");
    }

    private void RequestClose()
    {
        if (_isClosing || _application is null)
        {
            return;
        }

        _isClosing = true;
        _ = _application.Dispatcher.InvokeAsync(() =>
        {
            _taskbarIcon?.Dispose();
            _taskbarIcon = null;
            _application?.Shutdown();
        });
    }

    private static Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            return new Icon(iconPath);
        }

        var extracted = Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory);
        return extracted is not null ? (Icon)extracted.Clone() : SystemIcons.Application;
    }

    private Task<TrayHostControlResponse> HandleTrayControlRequestAsync(TrayHostControlRequest request)
    {
        if (request.Command == TrayHostControlCommand.Exit)
        {
            RequestClose();
        }

        return Task.FromResult(new TrayHostControlResponse
        {
            Success = true,
            Message = "Tray host command applied."
        });
    }
}
