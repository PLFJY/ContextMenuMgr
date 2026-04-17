using System.IO;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.TrayHost;

internal sealed class TrayHostRunner : IDisposable
{
    private const string TrayMutexName = @"Local\PLFJY.ContextMenuManagerPlus.TrayHost";

    private readonly TrayBackendPipeClient _backendPipeClient;
    private readonly FrontendActivationService _frontendActivationService;
    private readonly TrayHostLogger _logger;
    private readonly TrayLocalizationService _localization;

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    private NativeTrayHost? _trayHost;

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
            var trayIconPath = ResolveTrayIconPath();

            _trayHost = new NativeTrayHost(
                trayIconPath,
                _localization.Translate("Tray.Tooltip"),
                _localization.Translate("Tray.ShowMainWindow"),
                _localization.Translate("Tray.ExitFull"),
                ShowMainWindow,
                RequestBackendShutdown,
                OpenApprovals);

            _trayHost.Initialize();

            _backendPipeClient.NotificationReceived += OnNotificationReceived;
            _backendPipeClient.BackendUnavailable += OnBackendUnavailable;

            _controlServerCts = new CancellationTokenSource();
            _controlServer = new TrayHostControlServer(HandleTrayControlRequestAsync);
            _controlServer.Start(_controlServerCts.Token);

            _backendPipeClient.Start();

            return _trayHost.RunMessageLoop();
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

        _trayHost?.Dispose();
        _trayHost = null;

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
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;

        _ = Task.Run(async () =>
        {
            try
            {
                _frontendActivationService.TryShutdownFrontend();
                await Task.Delay(500);
                await _backendPipeClient.RequestBackendShutdownAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                await _logger.LogAsync($"Failed to request backend shutdown from tray host: {ex.Message}");
            }
            finally
            {
                _trayHost?.RequestClose();
            }
        });
    }

    private void OnNotificationReceived(object? sender, BackendNotification notification)
    {
        if (notification.Kind == PipeNotificationKind.ServiceStopping)
        {
            _trayHost?.RequestClose();
            return;
        }

        if (notification.Kind != PipeNotificationKind.ItemDetected || notification.Item is null)
        {
            return;
        }

        _pendingApprovalItemId = notification.Item.Id;

        _trayHost?.ShowNotification(
            _localization.Translate("Tray.PendingApprovalTitle"),
            _localization.Format("Tray.PendingApprovalMessage", notification.Item.DisplayName));
    }

    private void OnBackendUnavailable(object? sender, EventArgs e)
    {
        _ = _logger.LogAsync("Backend became unavailable while tray host stays alive.");
    }

    private static string? ResolveTrayIconPath()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        return File.Exists(iconPath) ? iconPath : null;
    }

    private Task<TrayHostControlResponse> HandleTrayControlRequestAsync(TrayHostControlRequest request)
    {
        if (request.Command == TrayHostControlCommand.Exit)
        {
            _trayHost?.RequestClose();
        }
        else if (request.Command == TrayHostControlCommand.ReloadLocalization)
        {
            ReloadLocalization();
        }

        return Task.FromResult(new TrayHostControlResponse
        {
            Success = true,
            Message = "Tray host command applied."
        });
    }

    private void ReloadLocalization()
    {
        _localization.Reload();
        _trayHost?.UpdateLocalization(
            _localization.Translate("Tray.Tooltip"),
            _localization.Translate("Tray.ShowMainWindow"),
            _localization.Translate("Tray.ExitFull"));
    }
}
