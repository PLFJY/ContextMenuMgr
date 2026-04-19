using System.IO;
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
    private readonly Lock _syncRoot = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FrontendSettingsService"/> class.
    /// </summary>
    public FrontendSettingsService()
    {
        _settingsPath = RuntimePaths.SettingsPath;
        TryMigrateLegacySettings();
        Current = Load();
    }

    /// <summary>
    /// Gets or sets the current.
    /// </summary>
    public FrontendSettings Current { get; private set; }

    public event EventHandler? SettingsChanged;

    public string SettingsPath => _settingsPath;

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

    private FrontendSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new FrontendSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<FrontendSettings>(json, JsonOptions) ?? new FrontendSettings();
        }
        catch
        {
            return new FrontendSettings();
        }
    }

    private void Save()
    {
        lock (_syncRoot)
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
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
            if (File.Exists(_settingsPath) || !File.Exists(RuntimePaths.LegacyFrontendSettingsPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.Copy(RuntimePaths.LegacyFrontendSettingsPath, _settingsPath, overwrite: false);
        }
        catch
        {
        }
    }
}
