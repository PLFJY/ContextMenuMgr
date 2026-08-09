using System.IO;
using System.Security;
using System.Text.Json;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the frontend Settings Service.
/// </summary>
public sealed class FrontendSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly RuntimeDataAclRepairClient? _aclRepairClient;
    private readonly Lock _syncRoot = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FrontendSettingsService"/> class.
    /// </summary>
    public FrontendSettingsService(RuntimeDataAclRepairClient aclRepairClient)
        : this(aclRepairClient, RuntimePaths.SettingsPath, migrateLegacySettings: true)
    {
    }

    /// <summary>
    /// Initializes a settings service for an explicit settings path. This keeps
    /// settings persistence independently testable without using runtime paths.
    /// </summary>
    public FrontendSettingsService(
        RuntimeDataAclRepairClient? aclRepairClient,
        string settingsPath,
        bool migrateLegacySettings = false)
    {
        _aclRepairClient = aclRepairClient;
        _settingsPath = settingsPath;
        if (migrateLegacySettings)
        {
            TryMigrateLegacySettings();
        }

        Current = Load();
    }

    /// <summary>
    /// Gets or sets the current.
    /// </summary>
    public FrontendSettings Current { get; private set; }

    public event EventHandler? SettingsChanged;

    public string SettingsPath => _settingsPath;

    public string? LastSaveError { get; private set; }

    public string GetContextMenuItemNote(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return string.Empty;
        }

        return Current.ContextMenuItemNotes.TryGetValue(itemId, out var note) ? note : string.Empty;
    }

    public void UpdateContextMenuItemNote(string itemId, string? note)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        var normalizedNote = note?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(normalizedNote))
        {
            Current.ContextMenuItemNotes.Remove(itemId);
        }
        else
        {
            Current.ContextMenuItemNotes[itemId] = normalizedNote;
        }

        Save();
    }

    /// <summary>
    /// Updates language.
    /// </summary>
    public void UpdateLanguage(AppLanguageOption language)
    {
        if (Current.Language == language)
        {
            return;
        }

        Current.Language = language;
        Save();
    }

    /// <summary>
    /// Updates theme.
    /// </summary>
    public void UpdateTheme(AppThemeOption theme)
    {
        if (Current.Theme == theme)
        {
            return;
        }

        Current.Theme = theme;
        Save();
    }

    /// <summary>
    /// Updates log Level.
    /// </summary>
    public void UpdateLogLevel(AppLogLevel logLevel)
    {
        if (Current.LogLevel == logLevel)
        {
            return;
        }

        Current.LogLevel = logLevel;
        Save();
    }

    /// <summary>
    /// Updates auto Start On Login.
    /// </summary>
    public void UpdateAutoStartOnLogin(bool autoStartOnLogin)
    {
        if (Current.AutoStartOnLogin == autoStartOnLogin)
        {
            return;
        }

        Current.AutoStartOnLogin = autoStartOnLogin;
        Save();
    }

    /// <summary>
    /// Updates show Tray Icon.
    /// </summary>
    public void UpdateShowTrayIcon(bool showTrayIcon)
    {
        if (Current.ShowTrayIcon == showTrayIcon)
        {
            return;
        }

        Current.ShowTrayIcon = showTrayIcon;
        Save();
    }

    /// <summary>
    /// Updates keep Background After Close.
    /// </summary>
    public void UpdateKeepBackgroundAfterClose(bool keepBackgroundAfterClose)
    {
        if (Current.KeepBackgroundAfterClose == keepBackgroundAfterClose)
        {
            return;
        }

        Current.KeepBackgroundAfterClose = keepBackgroundAfterClose;
        Save();
    }

    /// <summary>
    /// Updates lock New Context Menu Items.
    /// </summary>
    public void UpdateLockNewContextMenuItems(bool lockNewContextMenuItems)
    {
        if (Current.LockNewContextMenuItems == lockNewContextMenuItems)
        {
            return;
        }

        Current.LockNewContextMenuItems = lockNewContextMenuItems;
        Save();
    }

    public void UpdateWin11ModernContextMenuDisabled(bool disabled)
    {
        if (Current.Win11ModernContextMenuDisabled == disabled)
        {
            return;
        }

        Current.Win11ModernContextMenuDisabled = disabled;
        Save();
    }

    /// <summary>
    /// Updates hide Disabled Items.
    /// </summary>
    public void UpdateHideDisabledItems(bool hideDisabledItems)
    {
        if (Current.HideDisabledItems == hideDisabledItems)
        {
            return;
        }

        Current.HideDisabledItems = hideDisabledItems;
        Save();
    }

    /// <summary>
    /// Updates the shared context-menu list presentation mode.
    /// </summary>
    public void UpdateContextMenuListViewMode(ContextMenuListViewMode viewMode)
    {
        if (Current.ContextMenuListViewMode == viewMode)
        {
            return;
        }

        Current.ContextMenuListViewMode = viewMode;
        Save();
    }

    /// <summary>
    /// Updates open More Regedit.
    /// </summary>
    public void UpdateOpenMoreRegedit(bool openMoreRegedit)
    {
        if (Current.OpenMoreRegedit == openMoreRegedit)
        {
            return;
        }

        Current.OpenMoreRegedit = openMoreRegedit;
        Save();
    }

    /// <summary>
    /// Updates open More Explorer.
    /// </summary>
    public void UpdateOpenMoreExplorer(bool openMoreExplorer)
    {
        if (Current.OpenMoreExplorer == openMoreExplorer)
        {
            return;
        }

        Current.OpenMoreExplorer = openMoreExplorer;
        Save();
    }

    /// <summary>
    /// Updates main window placement.
    /// </summary>
    public void UpdateMainWindowPlacement(
        double left,
        double top,
        double width,
        double height,
        string state)
    {
        Current.MainWindowLeft = left;
        Current.MainWindowTop = top;
        Current.MainWindowWidth = width;
        Current.MainWindowHeight = height;
        Current.MainWindowState = state;
        Save();
    }

    private FrontendSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new FrontendSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<FrontendSettings>(json, JsonOptions) ?? new FrontendSettings();
            // TODO: ContextMenuItemNotes are registry item-bound runtime data.
            // Keep pure preferences portable, but move notes to host-bound state
            // or clear them on portable foreign-host detection in a follow-up.
            settings.ContextMenuItemNotes = settings.ContextMenuItemNotes is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(settings.ContextMenuItemNotes, StringComparer.OrdinalIgnoreCase);
            return settings;
        }
        catch
        {
            return new FrontendSettings();
        }
    }

    private void Save()
    {
        try
        {
            lock (_syncRoot)
            {
                SaveCore();
            }

            LastSaveError = null;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        catch (Exception ex) when (IsRuntimeDataAccessException(ex))
        {
            FrontendDebugLog.Error("FrontendSettingsService", ex, $"Saving settings failed due to runtime data access. Path={_settingsPath}");
        }

        var repairResult = RepairRuntimeDataAclForSynchronousSave();
        if (repairResult.Success)
        {
            try
            {
                lock (_syncRoot)
                {
                    SaveCore();
                }

                LastSaveError = null;
                SettingsChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            catch (Exception ex) when (IsRuntimeDataAccessException(ex))
            {
                LastSaveError = $"Settings save failed after runtime data ACL repair: {ex.Message}";
                FrontendDebugLog.Error("FrontendSettingsService", ex, $"Settings save retry failed after runtime data ACL repair. Path={_settingsPath}, RepairCode={repairResult.Code}, RepairDetail={repairResult.Detail}");
            }
        }
        else
        {
            LastSaveError = repairResult.Cancelled
                ? "Settings save skipped because runtime data ACL repair was cancelled."
                : $"Settings save failed because runtime data ACL repair failed: {repairResult.Detail}";
            FrontendDebugLog.Warning(
                "FrontendSettingsService",
                $"Runtime data ACL repair did not complete before settings retry. Success={repairResult.Success}, Cancelled={repairResult.Cancelled}, Code={repairResult.Code}, Detail={repairResult.Detail}");
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveCore()
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(Current, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }

    private RuntimeDataAclRepairClientResult RepairRuntimeDataAclForSynchronousSave()
    {
        if (_aclRepairClient is null)
        {
            return new RuntimeDataAclRepairClientResult(false, false, "ACL_REPAIR_UNAVAILABLE", "No runtime data ACL repair client is configured.");
        }

        try
        {
            // Save is intentionally synchronous because it is called from many
            // property setters. Blocking is limited to the rare ACL-repair path.
            return _aclRepairClient
                .RepairAsync(CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error("FrontendSettingsService", ex, "Runtime data ACL repair threw during synchronous settings save.");
            return new RuntimeDataAclRepairClientResult(false, false, "ACL_REPAIR_EXCEPTION", ex.ToString());
        }
    }

    private static bool IsRuntimeDataAccessException(Exception exception)
        => (exception is UnauthorizedAccessException
            or SecurityException)
            || exception is IOException ioException && IsAclRelatedIOException(ioException);

    private static bool IsAclRelatedIOException(IOException exception)
    {
        var win32Code = exception.HResult & 0xFFFF;
        return win32Code is 3 // ERROR_PATH_NOT_FOUND
            or 5 // ERROR_ACCESS_DENIED
            or 32 // ERROR_SHARING_VIOLATION
            or 53 // ERROR_BAD_NETPATH
            or 80 // ERROR_FILE_EXISTS
            or 183; // ERROR_ALREADY_EXISTS
    }

    /// <summary>
    /// Executes reset To Defaults.
    /// </summary>
    public void ResetToDefaults()
    {
        Current = new FrontendSettings();

        lock (_syncRoot)
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    File.Delete(_settingsPath);
                }
            }
            catch
            {
            }
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TryMigrateLegacySettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                return;
            }

            var sourcePath = File.Exists(RuntimePaths.LegacyFrontendSettingsPath)
                ? RuntimePaths.LegacyFrontendSettingsPath
                : RuntimePaths.PackageKind == RuntimePackageKind.Portable && File.Exists(RuntimePaths.LegacyProgramDataSettingsPath)
                    ? RuntimePaths.LegacyProgramDataSettingsPath
                    : null;

            if (string.IsNullOrWhiteSpace(sourcePath)
                || string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(_settingsPath), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.Copy(sourcePath, _settingsPath, overwrite: false);
        }
        catch
        {
        }
    }
}
