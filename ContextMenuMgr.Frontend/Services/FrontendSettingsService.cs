using System.IO;
using System.Text.Json;

namespace ContextMenuMgr.Frontend.Services;

public sealed class FrontendSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly Lock _syncRoot = new();

    public FrontendSettingsService()
    {
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ContextMenuMgr",
            "frontend-settings.json");
        Current = Load();
    }

    public FrontendSettings Current { get; private set; }

    public event EventHandler? SettingsChanged;

    public void UpdateLanguage(AppLanguageOption language)
    {
        if (Current.Language == language)
        {
            return;
        }

        Current.Language = language;
        Save();
    }

    public void UpdateTheme(AppThemeOption theme)
    {
        if (Current.Theme == theme)
        {
            return;
        }

        Current.Theme = theme;
        Save();
    }

    public void UpdateLogLevel(AppLogLevel logLevel)
    {
        if (Current.LogLevel == logLevel)
        {
            return;
        }

        Current.LogLevel = logLevel;
        Save();
    }

    public void UpdateLaunchMinimized(bool launchMinimized)
    {
        if (Current.LaunchMinimized == launchMinimized)
        {
            return;
        }

        Current.LaunchMinimized = launchMinimized;
        Save();
    }

    public void UpdateLockNewContextMenuItems(bool lockNewContextMenuItems)
    {
        if (Current.LockNewContextMenuItems == lockNewContextMenuItems)
        {
            return;
        }

        Current.LockNewContextMenuItems = lockNewContextMenuItems;
        Save();
    }

    public void UpdateHideDisabledItems(bool hideDisabledItems)
    {
        if (Current.HideDisabledItems == hideDisabledItems)
        {
            return;
        }

        Current.HideDisabledItems = hideDisabledItems;
        Save();
    }

    public void UpdateOpenMoreRegedit(bool openMoreRegedit)
    {
        if (Current.OpenMoreRegedit == openMoreRegedit)
        {
            return;
        }

        Current.OpenMoreRegedit = openMoreRegedit;
        Save();
    }

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
}
