using System.IO;
using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

public sealed class FrontendStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ContextMenuManager";

    public bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(RunValueName)?.ToString();
        return !string.IsNullOrWhiteSpace(value);
    }

    public void SetAutoStartEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null)
        {
            throw new InvalidOperationException("Unable to open the Windows Run registry key.");
        }

        if (!enabled)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException("Unable to resolve the frontend executable path.");
        }

        var command = $"\"{executablePath}\" --startup";
        key.SetValue(RunValueName, command, RegistryValueKind.String);
    }
}
