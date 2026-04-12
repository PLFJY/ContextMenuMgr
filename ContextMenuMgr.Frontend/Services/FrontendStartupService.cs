using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

public sealed class FrontendStartupService
{
    private const string PolicyKeyPath = @"Software\ContextMenuMgr\Frontend";
    private const string PolicyValueName = "StartWithWindows";
    private const string LegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValueName = "ContextMenuManager";

    public bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PolicyKeyPath, writable: false);
        var value = key?.GetValue(PolicyValueName);
        if (value is int intValue)
        {
            return intValue != 0;
        }

        if (value is string stringValue && int.TryParse(stringValue, out var parsed))
        {
            return parsed != 0;
        }

        using var legacyRunKey = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: false);
        return legacyRunKey?.GetValue(LegacyRunValueName) is string legacyCommand
               && !string.IsNullOrWhiteSpace(legacyCommand);
    }

    public void SetAutoStartEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PolicyKeyPath);
        if (key is null)
        {
            throw new InvalidOperationException("Unable to open the frontend startup policy registry key.");
        }

        key.SetValue(PolicyValueName, enabled ? 1 : 0, RegistryValueKind.DWord);

        // Clean up the legacy Run-based startup entry. The backend service now
        // owns tray startup so the frontend should no longer register itself
        // directly under the user's Run key.
        using var legacyRunKey = Registry.CurrentUser.CreateSubKey(LegacyRunKeyPath);
        legacyRunKey?.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
    }
}
