using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the settings Page View Model.
/// </summary>
public partial class SettingsPageViewModel : ObservableObject, IDisposable
{
    private readonly FrontendSettingsService _settingsService;
    private readonly FrontendStartupService _startupService;
    private readonly TrayHostProcessService _trayHostProcessService;
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly LocalizationService _localization;
    private readonly FrontendThemeService _themeService;
    private readonly ContextMenuItemActionsService _actionsService;
    private readonly ListPlaceholderDebugStateService _placeholderDebug;
    private readonly ExplorerRestartStateService _explorerRestartState;
    private readonly PortablePackageTrustService _portablePackageTrustService;
    private bool _suppressProtectionSync;
    private bool _suppressAutoStartSync;
    private bool _suppressTrayIconSync;
    private bool _suppressWin11ContextMenuSync;
    private bool _suppressThemeSync;
    private bool _pendingRegistryProtectionEnable;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsPageViewModel"/> class.
    /// </summary>
    public SettingsPageViewModel(
        FrontendSettingsService settingsService,
        FrontendStartupService startupService,
        TrayHostProcessService trayHostProcessService,
        ContextMenuWorkspaceService workspace,
        LocalizationService localization,
        FrontendThemeService themeService,
        ContextMenuItemActionsService actionsService,
        ListPlaceholderDebugStateService placeholderDebug,
        ExplorerRestartStateService explorerRestartState,
        PortablePackageTrustService portablePackageTrustService)
    {
        _settingsService = settingsService;
        _startupService = startupService;
        _trayHostProcessService = trayHostProcessService;
        _workspace = workspace;
        _localization = localization;
        _themeService = themeService;
        _actionsService = actionsService;
        _placeholderDebug = placeholderDebug;
        _explorerRestartState = explorerRestartState;
        _portablePackageTrustService = portablePackageTrustService;

        AvailableLanguages =
        [
            new LanguageOptionViewModel(AppLanguageOption.System, localization),
            new LanguageOptionViewModel(AppLanguageOption.ChineseSimplified, localization),
            new LanguageOptionViewModel(AppLanguageOption.ChineseTraditionalTaiwan, localization),
            new LanguageOptionViewModel(AppLanguageOption.EnglishUnitedStates, localization)
        ];

        AvailableThemes =
        [
            new ThemeOptionViewModel(AppThemeOption.System, localization),
            new ThemeOptionViewModel(AppThemeOption.Light, localization),
            new ThemeOptionViewModel(AppThemeOption.Dark, localization)
        ];

        AvailableLogLevels =
        [
            new LogLevelOptionViewModel(AppLogLevel.Information, localization),
            new LogLevelOptionViewModel(AppLogLevel.Warning, localization),
            new LogLevelOptionViewModel(AppLogLevel.Error, localization)
        ];

        SelectedLanguage = AvailableLanguages.FirstOrDefault(item => item.Option == _localization.SelectedLanguage) ?? AvailableLanguages[0];
        SelectedTheme = AvailableThemes.FirstOrDefault(item => item.Option == _themeService.GetThemePreference()) ?? AvailableThemes[0];
        SelectedLogLevel = AvailableLogLevels.FirstOrDefault(item => item.Option == _settingsService.Current.LogLevel) ?? AvailableLogLevels[1];

        // Initialize with default values to avoid blocking UI thread
        AutoStartOnLogin = false;
        ShowTrayIcon = _settingsService.Current.ShowTrayIcon;
        KeepBackgroundAfterClose = _settingsService.Current.KeepBackgroundAfterClose;
        LockNewContextMenuItems = _settingsService.Current.LockNewContextMenuItems;
        _suppressWin11ContextMenuSync = true;
        Win11ModernContextMenuDisabled = IsWin11ModernContextMenuSupported
            && _settingsService.Current.Win11ModernContextMenuDisabled;
        _suppressWin11ContextMenuSync = false;

        _localization.LanguageChanged += OnLanguageChanged;
        _themeService.ThemePreferenceChanged += OnThemePreferenceChanged;
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        RefreshLocalizedText();
        RefreshServiceState();

        // Load async settings in background to avoid blocking UI thread
        _ = LoadInitialSettingsAsync();
        _ = RefreshPortableRuntimeTrustWarningAsync();
    }

    /// <summary>
    /// Gets the available Languages.
    /// </summary>
    public ObservableCollection<LanguageOptionViewModel> AvailableLanguages { get; }

    /// <summary>
    /// Gets the available Themes.
    /// </summary>
    public ObservableCollection<ThemeOptionViewModel> AvailableThemes { get; }

    /// <summary>
    /// Gets the available Log Levels.
    /// </summary>
    public ObservableCollection<LogLevelOptionViewModel> AvailableLogLevels { get; }

    /// <summary>
    /// Gets or sets the selected Language.
    /// </summary>
    [ObservableProperty]
    public partial LanguageOptionViewModel? SelectedLanguage { get; set; }

    /// <summary>
    /// Gets or sets the selected Theme.
    /// </summary>
    [ObservableProperty]
    public partial ThemeOptionViewModel? SelectedTheme { get; set; }

    /// <summary>
    /// Gets or sets the selected Log Level.
    /// </summary>
    [ObservableProperty]
    public partial LogLevelOptionViewModel? SelectedLogLevel { get; set; }

    /// <summary>
    /// Gets or sets the auto Start On Login.
    /// </summary>
    [ObservableProperty]
    public partial bool AutoStartOnLogin { get; set; }

    /// <summary>
    /// Gets or sets whether the TrayHost notification-area icon is visible.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowTrayIcon { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether background After Close.
    /// </summary>
    [ObservableProperty]
    public partial bool KeepBackgroundAfterClose { get; set; }

    /// <summary>
    /// Gets or sets the lock New Context Menu Items.
    /// </summary>
    [ObservableProperty]
    public partial bool LockNewContextMenuItems { get; set; }

    [ObservableProperty]
    public partial bool Win11ModernContextMenuDisabled { get; set; }

    public bool IsWin11ModernContextMenuSupported { get; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>
    /// Gets or sets a value indicating whether uninstall Flyout Open.
    /// </summary>
    [ObservableProperty]
    public partial bool IsUninstallFlyoutOpen { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether registry protection Warning Flyout Open.
    /// </summary>
    [ObservableProperty]
    public partial bool IsRegistryProtectionWarningFlyoutOpen { get; set; }

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    /// <summary>
    /// Gets or sets the service State Text.
    /// </summary>
    [ObservableProperty]
    public partial string ServiceStateText { get; private set; } = string.Empty;

    public string LanguageLabel => _localization.Translate("LanguageLabel");

    public string ThemeLabel => _localization.Translate("ThemeLabel");

    public string LogLevelLabel => _localization.Translate("LogLevelLabel");

    public string StartupBehaviorTitle => _localization.Translate("StartupBehaviorTitle");

    public string AutoStartOnLoginLabel => _localization.Translate("Settings.StartWithWindows");

    public string AutoStartOnLoginDescription => _localization.Translate("Settings.StartWithWindows.Description");

    public string ShowTrayIconLabel => _localization.Translate("Settings.ShowTrayIcon");

    public string ShowTrayIconDescription => _localization.Translate("Settings.ShowTrayIcon.Description");

    public string KeepBackgroundAfterCloseLabel => _localization.Translate("Settings.KeepBackgroundAfterClose");

    public string KeepBackgroundAfterCloseDescription => _localization.Translate("Settings.KeepBackgroundAfterClose.Description");

    public string ProtectionTitle => _localization.Translate("ProtectionTitle");

    public string UtilitiesTitle => _localization.Translate("UtilitiesTitle");

    public string LockNewContextMenuItemsLabel => _localization.Translate("LockNewContextMenuItemsLabel");

    public string LockNewContextMenuItemsDescription => _localization.Translate("LockNewContextMenuItemsDescription");

    public string Win11ModernContextMenuDisabledLabel => _localization.Translate("Win11ModernContextMenuDisabledLabel");

    public string Win11ModernContextMenuDisabledDescription => _localization.Translate("Win11ModernContextMenuDisabledDescription");

    public string Win11ContextMenuSettingTitle => _localization.Translate("Win11ContextMenuSettingTitle");

    public string RegistryProtectionWarningTitle => _localization.Translate("RegistryProtectionWarningTitle");

    public string RegistryProtectionWarningText => _localization.Translate("RegistryProtectionWarningText");

    public string RegistryProtectionWarningConfirmText => _localization.Translate("DialogConfirm");

    public string RegistryProtectionWarningCloseText => _localization.Translate("DialogCancel");

    public string ServiceSettingsTitle => _localization.Translate("ServiceSettingsTitle");

    public string InstallOrRepairServiceText => _localization.Translate("InstallOrRepairService");

    public string ForceRepairServiceText => _localization.Translate("ForceRepairService");

    public string ForceRepairServiceTitle => _localization.Translate("ForceRepairServiceTitle");

    public string ForceRepairServicePrompt => _localization.Translate("ForceRepairServicePrompt");

    public string ServicePendingDeleteMessage => _localization.Translate("ServicePendingDeleteMessage");

    public string PortableRuntimeBlockedWarningTitle => _localization.Translate("PortableRuntimeBlockedWarningTitle");

    public string PortableRuntimeBlockedWarningText => _localization.Translate("PortableRuntimeBlockedWarningText");

    public string UnblockPortableFilesAndRetryText => _localization.Translate("UnblockPortableFilesAndRetry");

    public string UninstallServiceText => _localization.Translate("SettingsUninstallService");

    public string RefreshText => _localization.Translate("Refresh");

    public string RestartExplorerText => _localization.Translate("RestartExplorer");

    public string LocalFilesTitle => _localization.Translate("LocalFilesTitle");

    public string DebugToolsTitle => _localization.Translate("DebugToolsTitle");

    public string OpenLogsFolderText => _localization.Translate("OpenLogsFolder");

    public string OpenStateFolderText => _localization.Translate("OpenStateFolder");

    public string OpenConfigFolderText => _localization.Translate("OpenConfigFolder");

    public string ResetStateDatabaseText => _localization.Translate("ResetStateDatabase");

    public string ResetSettingsText => _localization.Translate("ResetSettings");

    public string ClearAllLogsText => _localization.Translate("ClearAllLogs");

    public string ListPlaceholderDebugTitle => _localization.Translate("ListPlaceholderDebugTitle");

    public string DebugSimulateLoadingText => _localization.Translate("DebugSimulateLoadingText");

    public string DebugSimulateEmptyText => _localization.Translate("DebugSimulateEmptyText");

    public string DebugClearSimulatedStateText => _localization.Translate("DebugClearSimulatedStateText");

    public string CancelText => _localization.Translate("DialogCancel");

    public string ConfirmUninstallText => _localization.Translate("SettingsUninstallService");

    public string UninstallFlyoutText => _localization.Translate("UninstallServicePrompt");

    public string RepositoryUrl => "https://github.com/PLFJY/ContextMenuMgr";

    public string LicenseText => "GPL v3.0 License";

    public string VersionLabel => "Version";

    public string VersionText => GetApplicationVersion();

    [ObservableProperty]
    public partial bool IsPortableRuntimeBlockedWarningVisible { get; private set; }

    [ObservableProperty]
    public partial string PortableRuntimeBlockedFilesText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPortableRuntimeUnblockInProgress { get; private set; }

    partial void OnSelectedLanguageChanged(LanguageOptionViewModel? value)
    {
        if (value is not null)
        {
            _localization.SelectedLanguage = value.Option;
            _ = NotifyTrayHostLocalizationChangedAsync();
        }
    }

    partial void OnSelectedThemeChanged(ThemeOptionViewModel? value)
    {
        if (_suppressThemeSync)
        {
            return;
        }

        if (value is not null)
        {
            _themeService.SetThemePreference(value.Option);
        }
    }

    partial void OnSelectedLogLevelChanged(LogLevelOptionViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        _settingsService.UpdateLogLevel(value.Option);
        FrontendDebugLog.Configure(value.Option);
        _ = _workspace.SyncBackendLogLevelAsync();
        _ = _workspace.SyncTrayHostLogLevelAsync();
    }

    partial void OnAutoStartOnLoginChanged(bool value)
    {
        if (_suppressAutoStartSync)
        {
            return;
        }

        _ = ApplyAutoStartOnLoginAsync(value);
    }

    partial void OnShowTrayIconChanged(bool value)
    {
        if (_suppressTrayIconSync)
        {
            return;
        }

        _ = ApplyShowTrayIconAsync(value);
    }

    partial void OnKeepBackgroundAfterCloseChanged(bool value)
    {
        _settingsService.UpdateKeepBackgroundAfterClose(value);
    }

    partial void OnLockNewContextMenuItemsChanged(bool value)
    {
        if (_suppressProtectionSync)
        {
            return;
        }

        if (_pendingRegistryProtectionEnable && !value)
        {
            _pendingRegistryProtectionEnable = false;
            IsRegistryProtectionWarningFlyoutOpen = false;
            return;
        }

        if (value && !_settingsService.Current.LockNewContextMenuItems)
        {
            _pendingRegistryProtectionEnable = true;
            IsRegistryProtectionWarningFlyoutOpen = true;
            return;
        }

        _pendingRegistryProtectionEnable = false;
        IsRegistryProtectionWarningFlyoutOpen = false;
        _ = UpdateRegistryProtectionSettingAsync(value);
    }

    partial void OnWin11ModernContextMenuDisabledChanged(bool value)
    {
        if (_suppressWin11ContextMenuSync)
        {
            return;
        }

        if (!IsWin11ModernContextMenuSupported)
        {
            _suppressWin11ContextMenuSync = true;
            Win11ModernContextMenuDisabled = false;
            _suppressWin11ContextMenuSync = false;
            return;
        }

        _ = ApplyWin11ModernContextMenuDisabledAsync(value);
    }

    partial void OnIsRegistryProtectionWarningFlyoutOpenChanged(bool value)
    {
        if (value || !_pendingRegistryProtectionEnable)
        {
            return;
        }

        _pendingRegistryProtectionEnable = false;
        _suppressProtectionSync = true;
        LockNewContextMenuItems = false;
        _suppressProtectionSync = false;
    }

    [RelayCommand]
    private async Task InstallOrRepairServiceAsync()
    {
        var result = await _workspace.InstallOrRepairServiceAsync();
        if (string.Equals(result.Code, "PORTABLE_RUNTIME_FILES_BLOCKED", StringComparison.OrdinalIgnoreCase))
        {
            await RefreshPortableRuntimeTrustWarningAsync();
            if (await FrontendMessageBox.ShowConfirmAsync(
                    PortableRuntimeBlockedWarningText,
                    PortableRuntimeBlockedWarningTitle,
                    UnblockPortableFilesAndRetryText,
                    CancelText))
            {
                await UnblockPortableFilesAndRetryAsync();
            }

            return;
        }

        if (!result.Success && !result.Cancelled)
        {
            await FrontendMessageBox.ShowErrorAsync(
                result.Detail,
                _localization.Translate("InstallOrRepairService"));
        }

        RefreshServiceState();
    }

    [RelayCommand]
    private async Task ForceRepairServiceAsync()
    {
        var confirmed = await FrontendMessageBox.ShowConfirmAsync(
            ForceRepairServicePrompt,
            ForceRepairServiceTitle,
            ForceRepairServiceText,
            CancelText);
        if (!confirmed)
        {
            return;
        }

        var removeResult = await _workspace.ForceRemoveServiceAsync();
        RefreshServiceState();
        if (removeResult.Cancelled)
        {
            return;
        }

        if (string.Equals(removeResult.Code, "SERVICE_PENDING_DELETE", StringComparison.OrdinalIgnoreCase))
        {
            await FrontendMessageBox.ShowInfoAsync(
                ServicePendingDeleteMessage,
                ForceRepairServiceTitle);
            return;
        }

        if (!removeResult.Success)
        {
            await FrontendMessageBox.ShowErrorAsync(
                removeResult.Detail,
                ForceRepairServiceTitle);
            return;
        }

        var installResult = await _workspace.InstallOrRepairServiceAsync();
        if (string.Equals(installResult.Code, "SERVICE_PENDING_DELETE", StringComparison.OrdinalIgnoreCase))
        {
            await FrontendMessageBox.ShowInfoAsync(
                ServicePendingDeleteMessage,
                ForceRepairServiceTitle);
            RefreshServiceState();
            return;
        }

        if (!installResult.Success && !installResult.Cancelled)
        {
            await FrontendMessageBox.ShowErrorAsync(
                installResult.Detail,
                ForceRepairServiceTitle);
        }

        RefreshServiceState();
    }

    [RelayCommand]
    private async Task UnblockPortableFilesAndRetryAsync()
    {
        if (IsPortableRuntimeUnblockInProgress)
        {
            return;
        }

        IsPortableRuntimeUnblockInProgress = true;
        try
        {
            var report = await _portablePackageTrustService.UnblockPortableRuntimeFilesAsync(CancellationToken.None);
            if (report.FailedFiles.Count > 0)
            {
                PortableRuntimeBlockedFilesText = string.Join(
                    Environment.NewLine,
                    report.FailedFiles.Select(static file => $"{Path.GetFileName(file.FilePath)}: {file.Error}"));
                IsPortableRuntimeBlockedWarningVisible = true;
                await FrontendMessageBox.ShowErrorAsync(
                    _localization.Format("PortableRuntimeUnblockFailed", PortableRuntimeBlockedFilesText),
                    PortableRuntimeBlockedWarningTitle);
                return;
            }

            await RefreshPortableRuntimeTrustWarningAsync();
            var result = await _workspace.InstallOrRepairServiceAsync();
            if (string.Equals(result.Code, "PORTABLE_RUNTIME_FILES_BLOCKED", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshPortableRuntimeTrustWarningAsync();
                return;
            }

            if (!result.Success && !result.Cancelled)
            {
                await FrontendMessageBox.ShowErrorAsync(
                    result.Detail,
                    _localization.Translate("InstallOrRepairService"));
            }

            RefreshServiceState();
        }
        finally
        {
            IsPortableRuntimeUnblockInProgress = false;
        }
    }

    [RelayCommand]
    private Task OpenUninstallServiceFlyoutAsync()
    {
        IsUninstallFlyoutOpen = true;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void CloseUninstallFlyout()
    {
        IsUninstallFlyoutOpen = false;
    }

    [RelayCommand]
    private void CloseRegistryProtectionWarningFlyout()
    {
        if (_pendingRegistryProtectionEnable)
        {
            _pendingRegistryProtectionEnable = false;
            _suppressProtectionSync = true;
            LockNewContextMenuItems = false;
            _suppressProtectionSync = false;
        }

        IsRegistryProtectionWarningFlyoutOpen = false;
    }

    [RelayCommand]
    private async Task ConfirmRegistryProtectionWarningAsync()
    {
        _pendingRegistryProtectionEnable = false;
        IsRegistryProtectionWarningFlyoutOpen = false;
        await UpdateRegistryProtectionSettingAsync(true);
    }

    [RelayCommand]
    private async Task ConfirmUninstallServiceAsync()
    {
        IsUninstallFlyoutOpen = false;
        var result = await _workspace.UninstallServiceAsync();
        if (!result.Success && !result.Cancelled)
        {
            await FrontendMessageBox.ShowErrorAsync(
                result.Detail,
                _localization.Translate("SettingsUninstallService"));
        }

        RefreshServiceState();
    }

    [RelayCommand]
    private void RefreshServiceState()
    {
        ServiceStateText = _localization.Format("ServiceStatusFormat", _workspace.GetServiceStatusText());
    }

    [RelayCommand]
    private Task RestartExplorerAsync()
    {
        return _actionsService.RestartExplorerAsync();
    }

    [RelayCommand]
    private Task OpenLogsFolderAsync()
        => OpenFolderAsync(RuntimePaths.LogsDirectory);

    [RelayCommand]
    private Task OpenStateFolderAsync()
        => OpenFolderAsync(RuntimePaths.DataDirectory);

    [RelayCommand]
    private Task OpenConfigFolderAsync()
        => OpenFolderAsync(RuntimePaths.DeletedBackupsDirectory);

    [RelayCommand]
    private async Task ResetStateDatabaseAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _workspace.ResetStateDatabaseAsync(cts.Token);

            await FrontendMessageBox.ShowInfoAsync(
                _localization.Translate("MaintenanceResetStateSucceeded"),
                _localization.Translate("DebugToolsTitle"));
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                _localization.Translate("DebugToolsTitle"));
        }
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        try
        {
            _settingsService.ResetToDefaults();

            _suppressAutoStartSync = true;
            AutoStartOnLogin = false;
            _suppressAutoStartSync = false;

            _suppressTrayIconSync = true;
            ShowTrayIcon = true;
            _settingsService.UpdateShowTrayIcon(true);
            _suppressTrayIconSync = false;

            // Use async version to avoid blocking UI thread
            try
            {
                await _startupService.SetAutoStartEnabledAsync(false, CancellationToken.None, showTrayIcon: true);
            }
            catch
            {
                // Ignore errors during reset
            }

            await TryApplyRuntimeTrayIconVisibilityAsync(true);

            if (_workspace.IsServiceInstalled())
            {
                await _workspace.SetServiceAutoStartEnabledAsync(false);
            }

            _suppressProtectionSync = true;
            LockNewContextMenuItems = false;
            _suppressProtectionSync = false;
            try
            {
                await _workspace.SetRegistryProtectionSettingAsync(false);
            }
            catch
            {
            }

            _suppressWin11ContextMenuSync = true;
            Win11ModernContextMenuDisabled = false;
            _suppressWin11ContextMenuSync = false;
            try
            {
                if (IsWin11ModernContextMenuSupported)
                {
                    await _workspace.SetWin11ModernContextMenuDisabledAsync(false);
                }
            }
            catch
            {
            }

            KeepBackgroundAfterClose = false;

            _localization.SelectedLanguage = AppLanguageOption.System;
            SelectedLanguage = AvailableLanguages.FirstOrDefault(item => item.Option == AppLanguageOption.System) ?? AvailableLanguages[0];

            _themeService.SetThemePreference(AppThemeOption.System);
            SetSelectedThemeFromPreference(AppThemeOption.System);

            FrontendDebugLog.Configure(AppLogLevel.Warning);
            SelectedLogLevel = AvailableLogLevels.FirstOrDefault(item => item.Option == AppLogLevel.Warning) ?? AvailableLogLevels[0];
            await _workspace.SyncBackendLogLevelAsync();
            await _workspace.SyncTrayHostLogLevelAsync();

            await NotifyTrayHostLocalizationChangedAsync();

            await FrontendMessageBox.ShowInfoAsync(
                _localization.Translate("MaintenanceResetSettingsSucceeded"),
                _localization.Translate("DebugToolsTitle"));
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                _localization.Translate("DebugToolsTitle"));
        }
    }

    [RelayCommand]
    private async Task ClearAllLogsAsync()
    {
        try
        {
            Directory.CreateDirectory(RuntimePaths.LogsDirectory);
            foreach (var path in new[]
                     {
                         RuntimePaths.FrontendDebugLogPath,
                         RuntimePaths.FrontendCrashLogPath,
                         RuntimePaths.BackendLogPath,
                         RuntimePaths.TrayHostLogPath
                     })
            {
                TryTruncateFile(path);
            }

            await FrontendMessageBox.ShowInfoAsync(
                _localization.Translate("MaintenanceClearLogsSucceeded"),
                _localization.Translate("DebugToolsTitle"));
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                _localization.Translate("DebugToolsTitle"));
        }
    }

    [RelayCommand]
    private void SimulateListLoadingState()
    {
        _placeholderDebug.SimulateLoading();
    }

    [RelayCommand]
    private void SimulateListEmptyState()
    {
        _placeholderDebug.SimulateEmpty();
    }

    [RelayCommand]
    private void ClearListPlaceholderDebugState()
    {
        _placeholderDebug.Clear();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedText();
        RefreshServiceState();
        OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(ThemeLabel));
        OnPropertyChanged(nameof(LogLevelLabel));
        OnPropertyChanged(nameof(StartupBehaviorTitle));
        OnPropertyChanged(nameof(AutoStartOnLoginLabel));
        OnPropertyChanged(nameof(AutoStartOnLoginDescription));
        OnPropertyChanged(nameof(ShowTrayIconLabel));
        OnPropertyChanged(nameof(ShowTrayIconDescription));
        OnPropertyChanged(nameof(KeepBackgroundAfterCloseLabel));
        OnPropertyChanged(nameof(KeepBackgroundAfterCloseDescription));
        OnPropertyChanged(nameof(ProtectionTitle));
        OnPropertyChanged(nameof(UtilitiesTitle));
        OnPropertyChanged(nameof(LockNewContextMenuItemsLabel));
        OnPropertyChanged(nameof(LockNewContextMenuItemsDescription));
        OnPropertyChanged(nameof(Win11ModernContextMenuDisabledLabel));
        OnPropertyChanged(nameof(Win11ModernContextMenuDisabledDescription));
        OnPropertyChanged(nameof(Win11ContextMenuSettingTitle));
        OnPropertyChanged(nameof(RegistryProtectionWarningTitle));
        OnPropertyChanged(nameof(RegistryProtectionWarningText));
        OnPropertyChanged(nameof(RegistryProtectionWarningConfirmText));
        OnPropertyChanged(nameof(RegistryProtectionWarningCloseText));
        OnPropertyChanged(nameof(ServiceSettingsTitle));
        OnPropertyChanged(nameof(InstallOrRepairServiceText));
        OnPropertyChanged(nameof(ForceRepairServiceText));
        OnPropertyChanged(nameof(ForceRepairServiceTitle));
        OnPropertyChanged(nameof(ForceRepairServicePrompt));
        OnPropertyChanged(nameof(ServicePendingDeleteMessage));
        OnPropertyChanged(nameof(PortableRuntimeBlockedWarningTitle));
        OnPropertyChanged(nameof(PortableRuntimeBlockedWarningText));
        OnPropertyChanged(nameof(UnblockPortableFilesAndRetryText));
        OnPropertyChanged(nameof(UninstallServiceText));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(RestartExplorerText));
        OnPropertyChanged(nameof(LocalFilesTitle));
        OnPropertyChanged(nameof(DebugToolsTitle));
        OnPropertyChanged(nameof(OpenLogsFolderText));
        OnPropertyChanged(nameof(OpenStateFolderText));
        OnPropertyChanged(nameof(OpenConfigFolderText));
        OnPropertyChanged(nameof(ResetStateDatabaseText));
        OnPropertyChanged(nameof(ResetSettingsText));
        OnPropertyChanged(nameof(ClearAllLogsText));
        OnPropertyChanged(nameof(ListPlaceholderDebugTitle));
        OnPropertyChanged(nameof(DebugSimulateLoadingText));
        OnPropertyChanged(nameof(DebugSimulateEmptyText));
        OnPropertyChanged(nameof(DebugClearSimulatedStateText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(ConfirmUninstallText));
        OnPropertyChanged(nameof(UninstallFlyoutText));
    }

    private void OnThemePreferenceChanged(object? sender, ThemePreferenceChangedEventArgs e)
    {
        SetSelectedThemeFromPreference(e.NewPreference);
    }

    private void OnApplicationThemeChanged(ApplicationTheme currentApplicationTheme, Color systemAccent)
    {
        SetSelectedThemeFromPreference(_themeService.GetThemePreference());
    }

    private void SetSelectedThemeFromPreference(AppThemeOption preference)
    {
        var selectedTheme = AvailableThemes.FirstOrDefault(item => item.Option == preference) ?? AvailableThemes[0];
        if (ReferenceEquals(SelectedTheme, selectedTheme))
        {
            return;
        }

        _suppressThemeSync = true;
        SelectedTheme = selectedTheme;
        _suppressThemeSync = false;
    }

    private void RefreshLocalizedText()
    {
        Title = _localization.Translate("SettingsTitle");
    }

    private async Task LoadRegistryProtectionSettingAsync()
    {
        try
        {
            var enabled = await _workspace.GetRegistryProtectionSettingAsync();
            _suppressProtectionSync = true;
            LockNewContextMenuItems = enabled;
            _settingsService.UpdateLockNewContextMenuItems(enabled);
        }
        catch
        {
        }
        finally
        {
            _suppressProtectionSync = false;
        }
    }

    private async Task LoadWin11ModernContextMenuSettingAsync()
    {
        if (!IsWin11ModernContextMenuSupported)
        {
            _suppressWin11ContextMenuSync = true;
            Win11ModernContextMenuDisabled = false;
            _settingsService.UpdateWin11ModernContextMenuDisabled(false);
            _suppressWin11ContextMenuSync = false;
            return;
        }

        try
        {
            var disabled = await _workspace.GetWin11ModernContextMenuDisabledAsync();
            _suppressWin11ContextMenuSync = true;
            Win11ModernContextMenuDisabled = disabled;
            _settingsService.UpdateWin11ModernContextMenuDisabled(disabled);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning("SettingsPageViewModel", $"Failed to load Windows 11 context menu setting: {ex.Message}");
        }
        finally
        {
            _suppressWin11ContextMenuSync = false;
        }
    }

    /// <summary>
    /// Loads initial settings asynchronously to avoid blocking UI thread.
    /// This method is called fire-and-forget from the constructor.
    /// </summary>
    private async Task LoadInitialSettingsAsync()
    {
        try
        {
            // Load auto-start setting from backend (async, non-blocking)
            var autoStartEnabled = await _startupService.IsAutoStartEnabledAsync(CancellationToken.None);
            _suppressAutoStartSync = true;
            AutoStartOnLogin = autoStartEnabled;
            _suppressAutoStartSync = false;
            _settingsService.UpdateAutoStartOnLogin(autoStartEnabled);

            // Load registry protection setting (async, non-blocking)
            await LoadRegistryProtectionSettingAsync();

            // Registry is the source of truth; frontend settings only mirror it.
            await LoadWin11ModernContextMenuSettingAsync();
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning("SettingsPageViewModel", $"Failed to load initial settings: {ex.Message}");
        }
    }

    private async Task UpdateRegistryProtectionSettingAsync(bool value)
    {
        var previous = _settingsService.Current.LockNewContextMenuItems;

        try
        {
            var actualValue = await _workspace.SetRegistryProtectionSettingAsync(value);
            _settingsService.UpdateLockNewContextMenuItems(actualValue);

            if (actualValue != value)
            {
                _suppressProtectionSync = true;
                LockNewContextMenuItems = actualValue;
            }
        }
        catch (Exception ex)
        {
            _suppressProtectionSync = true;
            LockNewContextMenuItems = previous;
            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                _localization.Translate("ProtectionTitle"));
        }
        finally
        {
            _suppressProtectionSync = false;
        }
    }

    private async Task ApplyWin11ModernContextMenuDisabledAsync(bool value)
    {
        var previous = _settingsService.Current.Win11ModernContextMenuDisabled;
        _settingsService.UpdateWin11ModernContextMenuDisabled(value);

        try
        {
            await _workspace.SetWin11ModernContextMenuDisabledAsync(value);
            _explorerRestartState.MarkRequired();
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("SettingsPageViewModel", ex, "Failed to update Windows 11 context menu setting.");
            _settingsService.UpdateWin11ModernContextMenuDisabled(previous);
            _suppressWin11ContextMenuSync = true;
            Win11ModernContextMenuDisabled = previous;
            _suppressWin11ContextMenuSync = false;
            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                Win11ContextMenuSettingTitle);
        }
    }

    private async Task OpenFolderAsync(string folderPath)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folderPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                _localization.Translate("LocalFilesTitle"));
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryTruncateFile(string path)
    {
        try
        {
            File.WriteAllText(path, string.Empty);
        }
        catch
        {
        }
    }

    private async Task NotifyTrayHostLocalizationChangedAsync()
    {
        try
        {
            await _trayHostProcessService.RequestReloadLocalizationAsync(CancellationToken.None);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        _themeService.ThemePreferenceChanged -= OnThemePreferenceChanged;
        ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
        foreach (var item in AvailableLanguages)
        {
            item.Dispose();
        }

        foreach (var item in AvailableThemes)
        {
            item.Dispose();
        }

        foreach (var item in AvailableLogLevels)
        {
            item.Dispose();
        }
    }

    private static string GetApplicationVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "Unknown";
    }

    private async Task ApplyAutoStartOnLoginAsync(bool value)
    {
        try
        {
            // Use async version to avoid blocking UI thread
            await _startupService.SetAutoStartEnabledAsync(value, CancellationToken.None, ShowTrayIcon);
            _settingsService.UpdateAutoStartOnLogin(value);

            if (_workspace.IsServiceInstalled())
            {
                var result = await _workspace.SetServiceAutoStartEnabledAsync(value);
                if (!result.Success && !result.Cancelled)
                {
                    throw new InvalidOperationException(result.Detail);
                }
            }
        }
        catch (Exception ex)
        {
            // Use async version to avoid blocking UI thread
            var actualValue = await _startupService.IsAutoStartEnabledAsync(CancellationToken.None);
            _settingsService.UpdateAutoStartOnLogin(actualValue);
            _suppressAutoStartSync = true;
            AutoStartOnLogin = actualValue;
            _suppressAutoStartSync = false;
            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                _localization.Translate("StartupBehaviorTitle"));
        }
    }

    private async Task RefreshPortableRuntimeTrustWarningAsync()
    {
        try
        {
            var report = await _portablePackageTrustService.ScanPortableRuntimeFilesAsync(CancellationToken.None);
            IsPortableRuntimeBlockedWarningVisible = report.BlockedCount > 0;
            PortableRuntimeBlockedFilesText = report.BlockedCount == 0
                ? string.Empty
                : string.Join(Environment.NewLine, report.BlockedFiles.Select(static file => file.RelativePath));
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Warning("SettingsPageViewModel", $"Failed to scan portable runtime file trust state: {ex.Message}");
        }
    }


    private async Task ApplyShowTrayIconAsync(bool value)
    {
        var previous = _settingsService.Current.ShowTrayIcon;
        _settingsService.UpdateShowTrayIcon(value);

        try
        {
            await _startupService.SetTrayIconPolicyAsync(value, CancellationToken.None);
            await TryApplyRuntimeTrayIconVisibilityAsync(value);
        }
        catch (Exception ex)
        {
            _settingsService.UpdateShowTrayIcon(previous);
            _suppressTrayIconSync = true;
            ShowTrayIcon = previous;
            _suppressTrayIconSync = false;
            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                _localization.Translate("StartupBehaviorTitle"));
        }
    }

    private async Task TryApplyRuntimeTrayIconVisibilityAsync(bool visible)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            _ = await _trayHostProcessService.SetTrayIconVisibleAsync(visible, cts.Token);
        }
        catch
        {
            // TrayHost may not be running; the persisted setting applies next launch.
        }
    }
}
