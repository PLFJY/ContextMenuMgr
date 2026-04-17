using System.IO;
using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

public sealed class FrontendStartupService
{
    private const string PolicyKeyPath = @"Software\ContextMenuMgr\Frontend";
    private const string PolicyValueName = "StartWithWindows";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ContextMenuManager.TrayHost";
    private readonly string _trayHostExecutablePath = Path.Combine(AppContext.BaseDirectory, "ContextMenuManager.TrayHost.exe");

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

        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return runKey?.GetValue(RunValueName) is string command
               && !string.IsNullOrWhiteSpace(command);
    }

    public void SetAutoStartEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PolicyKeyPath);
        if (key is null)
        {
            throw new InvalidOperationException("Unable to open the frontend startup policy registry key.");
        }

        key.SetValue(PolicyValueName, enabled ? 1 : 0, RegistryValueKind.DWord);

        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (runKey is null)
        {
            throw new InvalidOperationException("Unable to open the Windows startup registry key.");
        }

        if (enabled)
        {
            if (!File.Exists(_trayHostExecutablePath))
            {
                throw new InvalidOperationException("ContextMenuManager.TrayHost.exe was not found.");
            }

            runKey.SetValue(RunValueName, $"\"{_trayHostExecutablePath}\"", RegistryValueKind.String);
        }
        else
        {
            runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }
}
