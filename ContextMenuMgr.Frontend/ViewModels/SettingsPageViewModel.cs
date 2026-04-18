using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class SettingsPageViewModel : ObservableObject, IDisposable
{
    private readonly FrontendSettingsService _settingsService;
    private readonly FrontendStartupService _startupService;
    private readonly TrayHostProcessService _trayHostProcessService;
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly LocalizationService _localization;
    private readonly ThemeService _themeService;
    private readonly ContextMenuItemActionsService _actionsService;
    private bool _suppressProtectionSync;
    private bool _suppressAutoStartSync;

    public SettingsPageViewModel(
        FrontendSettingsService settingsService,
        FrontendStartupService startupService,
        TrayHostProcessService trayHostProcessService,
        ContextMenuWorkspaceService workspace,
        LocalizationService localization,
        ThemeService themeService,
        ContextMenuItemActionsService actionsService)
    {
        _settingsService = settingsService;
        _startupService = startupService;
        _trayHostProcessService = trayHostProcessService;
        _workspace = workspace;
        _localization = localization;
        _themeService = themeService;
        _actionsService = actionsService;

        AvailableLanguages =
        [
            new LanguageOptionViewModel(AppLanguageOption.System, localization),
            new LanguageOptionViewModel(AppLanguageOption.ChineseSimplified, localization),
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
        SelectedTheme = AvailableThemes.FirstOrDefault(item => item.Option == _themeService.CurrentTheme) ?? AvailableThemes[0];
        SelectedLogLevel = AvailableLogLevels.FirstOrDefault(item => item.Option == _settingsService.Current.LogLevel) ?? AvailableLogLevels[1];
        _suppressAutoStartSync = true;
        AutoStartOnLogin = _startupService.IsAutoStartEnabled();
        _suppressAutoStartSync = false;
        _settingsService.UpdateAutoStartOnLogin(AutoStartOnLogin);
        KeepBackgroundAfterClose = _settingsService.Current.KeepBackgroundAfterClose;
        LockNewContextMenuItems = _settingsService.Current.LockNewContextMenuItems;

        _localization.LanguageChanged += OnLanguageChanged;
        RefreshLocalizedText();
        RefreshServiceState();
        _ = LoadRegistryProtectionSettingAsync();
    }

    public ObservableCollection<LanguageOptionViewModel> AvailableLanguages { get; }

    public ObservableCollection<ThemeOptionViewModel> AvailableThemes { get; }

    public ObservableCollection<LogLevelOptionViewModel> AvailableLogLevels { get; }

    [ObservableProperty]
    public partial LanguageOptionViewModel? SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial ThemeOptionViewModel? SelectedTheme { get; set; }

    [ObservableProperty]
    public partial LogLevelOptionViewModel? SelectedLogLevel { get; set; }

    [ObservableProperty]
    public partial bool AutoStartOnLogin { get; set; }

    [ObservableProperty]
    public partial bool KeepBackgroundAfterClose { get; set; }

    [ObservableProperty]
    public partial bool LockNewContextMenuItems { get; set; }

    [ObservableProperty]
    public partial bool IsUninstallFlyoutOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ServiceStateText { get; private set; } = string.Empty;

    public string LanguageLabel => _localization.Translate("LanguageLabel");

    public string ThemeLabel => _localization.Translate("ThemeLabel");

    public string LogLevelLabel => _localization.Translate("LogLevelLabel");

    public string StartupBehaviorTitle => _localization.Translate("StartupBehaviorTitle");

    public string AutoStartOnLoginLabel => _localization.Translate("Settings.StartWithWindows");

    public string AutoStartOnLoginDescription => _localization.Translate("Settings.StartWithWindows.Description");

    public string KeepBackgroundAfterCloseLabel => _localization.Translate("Settings.KeepBackgroundAfterClose");

    public string KeepBackgroundAfterCloseDescription => _localization.Translate("Settings.KeepBackgroundAfterClose.Description");

    public string ProtectionTitle => _localization.Translate("ProtectionTitle");

    public string UtilitiesTitle => _localization.Translate("UtilitiesTitle");

    public string LockNewContextMenuItemsLabel => _localization.Translate("LockNewContextMenuItemsLabel");

    public string LockNewContextMenuItemsDescription => _localization.Translate("LockNewContextMenuItemsDescription");

    public string ServiceSettingsTitle => _localization.Translate("ServiceSettingsTitle");

    public string InstallOrRepairServiceText => _localization.Translate("InstallOrRepairService");

    public string UninstallServiceText => _localization.Translate("SettingsUninstallService");

    public string RefreshText => _localization.Translate("Refresh");

    public string RestartExplorerText => _localization.Translate("RestartExplorer");

    public string LocalFilesTitle => _localization.Translate("LocalFilesTitle");

    public string OpenLogsFolderText => _localization.Translate("OpenLogsFolder");

    public string OpenStateFolderText => _localization.Translate("OpenStateFolder");

    public string OpenConfigFolderText => _localization.Translate("OpenConfigFolder");

    public string CancelText => _localization.Translate("DialogCancel");

    public string ConfirmUninstallText => _localization.Translate("SettingsUninstallService");

    public string UninstallFlyoutText => _localization.Translate("UninstallServicePrompt");

    public string RepositoryUrl => "https://github.com/PLFJY/ContextMenuMgr";

    public string LicenseText => "GPL v3.0 License";

    public string VersionLabel => "Version";

    public string VersionText => GetApplicationVersion();

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
        if (value is not null)
        {
            _themeService.ApplyTheme(value.Option);
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
    }

    partial void OnAutoStartOnLoginChanged(bool value)
    {
        if (_suppressAutoStartSync)
        {
            return;
        }

        _ = ApplyAutoStartOnLoginAsync(value);
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

        _ = UpdateRegistryProtectionSettingAsync(value);
    }

    [RelayCommand]
    private async Task InstallOrRepairServiceAsync()
    {
        var result = await _workspace.InstallOrRepairServiceAsync();
        if (!result.Success && !result.Cancelled)
        {
            await FrontendMessageBox.ShowErrorAsync(
                result.Detail,
                _localization.Translate("InstallOrRepairService"));
        }

        RefreshServiceState();
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
        => OpenFolderAsync(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ContextMenuMgr",
                "Logs"));

    [RelayCommand]
    private Task OpenStateFolderAsync()
        => OpenFolderAsync(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ContextMenuMgr",
                "Data"));

    [RelayCommand]
    private Task OpenConfigFolderAsync()
        => OpenFolderAsync(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ContextMenuMgr"));

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
        OnPropertyChanged(nameof(KeepBackgroundAfterCloseLabel));
        OnPropertyChanged(nameof(KeepBackgroundAfterCloseDescription));
        OnPropertyChanged(nameof(ProtectionTitle));
        OnPropertyChanged(nameof(UtilitiesTitle));
        OnPropertyChanged(nameof(LockNewContextMenuItemsLabel));
        OnPropertyChanged(nameof(LockNewContextMenuItemsDescription));
        OnPropertyChanged(nameof(ServiceSettingsTitle));
        OnPropertyChanged(nameof(InstallOrRepairServiceText));
        OnPropertyChanged(nameof(UninstallServiceText));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(RestartExplorerText));
        OnPropertyChanged(nameof(LocalFilesTitle));
        OnPropertyChanged(nameof(OpenLogsFolderText));
        OnPropertyChanged(nameof(OpenStateFolderText));
        OnPropertyChanged(nameof(OpenConfigFolderText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(ConfirmUninstallText));
        OnPropertyChanged(nameof(UninstallFlyoutText));
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

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
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
            _startupService.SetAutoStartEnabled(value);
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
            var actualValue = _startupService.IsAutoStartEnabled();
            _settingsService.UpdateAutoStartOnLogin(actualValue);
            _suppressAutoStartSync = true;
            AutoStartOnLogin = actualValue;
            _suppressAutoStartSync = false;
            await FrontendMessageBox.ShowErrorAsync(
                ex.Message,
                _localization.Translate("StartupBehaviorTitle"));
        }
    }
}
