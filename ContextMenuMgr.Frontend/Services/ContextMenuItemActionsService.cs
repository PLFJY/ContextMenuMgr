using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.ViewModels;
using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the context Menu Item Actions Service.
/// </summary>
public sealed class ContextMenuItemActionsService
{
    private readonly LocalizationService _localization;
    private readonly FrontendSettingsService _settingsService;
    private readonly IBackendClient _backendClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextMenuItemActionsService"/> class.
    /// </summary>
    public ContextMenuItemActionsService(LocalizationService localization, FrontendSettingsService settingsService, IBackendClient backendClient)
    {
        _localization = localization;
        _settingsService = settingsService;
        _backendClient = backendClient;
    }

    /// <summary>
    /// Opens web Search Async.
    /// </summary>
    public Task OpenWebSearchAsync(ContextMenuItemViewModel item)
    {
        return RunActionAsync(
            async () =>
            {
                var query = !string.IsNullOrWhiteSpace(item.DisplayName)
                    ? item.DisplayName
                    : item.KeyName;
                var url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}";
                OpenShellTarget(url);
                await Task.CompletedTask;
            },
            "DetailsSearchOnline");
    }

    /// <summary>
    /// Shows command Text Async.
    /// </summary>
    public async Task ShowCommandTextAsync(ContextMenuItemViewModel item)
    {
        await RunActionAsync(
            async () =>
            {
                if (string.IsNullOrWhiteSpace(item.Entry.CommandText))
                {
                    await FrontendMessageBox.ShowInfoAsync(
                        _localization.Translate("CommandTextUnavailable"),
                        _localization.Translate("DetailsCommandText"));
                    return;
                }

                await FrontendMessageBox.ShowInfoAsync(
                    item.Entry.CommandText,
                    _localization.Translate("DetailsCommandText"));
            },
            "DetailsCommandText");
    }

    /// <summary>
    /// Opens file Properties Async.
    /// </summary>
    public Task OpenFilePropertiesAsync(ContextMenuItemViewModel item)
    {
        return RunActionAsync(
            async () =>
            {
                if (string.IsNullOrWhiteSpace(item.Entry.FilePath))
                {
                    await ShowMissingTargetAsync("ModulePathUnavailable", "DetailsFileProperties");
                    return;
                }

                if (!ShowPropertiesDialog(item.Entry.FilePath))
                {
                    throw new InvalidOperationException(_localization.Translate("DetailsActionFailed"));
                }

                await Task.CompletedTask;
            },
            "DetailsFileProperties");
    }

    /// <summary>
    /// Opens file Location Async.
    /// </summary>
    public Task OpenFileLocationAsync(ContextMenuItemViewModel item)
    {
        return RunActionAsync(
            async () =>
            {
                if (string.IsNullOrWhiteSpace(item.Entry.FilePath))
                {
                    await ShowMissingTargetAsync("ModulePathUnavailable", "DetailsFileLocation");
                    return;
                }

                var targetPath = item.Entry.FilePath;
                var parentDirectory = Path.GetDirectoryName(targetPath);
                var useSeparateWindow = _settingsService.Current.OpenMoreExplorer;
                var startInfo = File.Exists(targetPath)
                    ? new ProcessStartInfo(
                        "explorer.exe",
                        useSeparateWindow ? $"/n,/select,\"{targetPath}\"" : $"/select,\"{targetPath}\"")
                    {
                        UseShellExecute = true
                    }
                    : new ProcessStartInfo(
                        "explorer.exe",
                        useSeparateWindow ? $"/n,\"{parentDirectory ?? targetPath}\"" : $"\"{parentDirectory ?? targetPath}\"")
                    {
                        UseShellExecute = true
                    };
                Process.Start(startInfo);
            },
            "DetailsFileLocation");
    }

    /// <summary>
    /// Opens registry Location Async.
    /// </summary>
    public Task OpenRegistryLocationAsync(ContextMenuItemViewModel item)
    {
        return RunActionAsync(
            async () =>
            {
                if (string.IsNullOrWhiteSpace(item.Entry.RegistryPath))
                {
                    await ShowMissingTargetAsync("NoRegistryPath", "DetailsRegistryLocation");
                    return;
                }

                OpenRegistryEditor(GetClassesRootPath(item.Entry.RegistryPath));
            },
            "DetailsRegistryLocation");
    }

    /// <summary>
    /// Opens clsid Location Async.
    /// </summary>
    public Task OpenClsidLocationAsync(ContextMenuItemViewModel item)
    {
        return RunActionAsync(
            async () =>
            {
                if (!item.HasClsidLocation)
                {
                    await ShowMissingTargetAsync("ClsidLocationUnavailable", "DetailsClsidLocation");
                    return;
                }

                OpenRegistryEditor($@"HKEY_CLASSES_ROOT\CLSID\{item.Entry.HandlerClsid}");
            },
            "DetailsClsidLocation");
    }

    /// <summary>
    /// Executes export Registry Async.
    /// </summary>
    public async Task ExportRegistryAsync(ContextMenuItemViewModel item)
    {
        await RunActionAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(item.Entry.RegistryPath))
            {
                await FrontendMessageBox.ShowInfoAsync(
                    _localization.Translate("NoRegistryPath"),
                    _localization.Translate("DetailsExportRegistry"));
                return;
            }

            var fileDialog = new SaveFileDialog
            {
                Filter = "Registry Files (*.reg)|*.reg",
                FileName = $"{SanitizeFileName(item.DisplayName)}.reg",
                DefaultExt = ".reg"
            };

            if (fileDialog.ShowDialog(System.Windows.Application.Current?.MainWindow) != true)
            {
                return;
            }

            var process = Process.Start(new ProcessStartInfo("reg.exe", $"export \"{GetClassesRootPath(item.Entry.RegistryPath)}\" \"{fileDialog.FileName}\" /y")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                throw new InvalidOperationException("Failed to start reg.exe.");
            }

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(_localization.Translate("ExportRegistryFailed"));
            }
        }, "DetailsExportRegistry");
    }

    /// <summary>
    /// Executes restart Explorer Async.
    /// </summary>
    public async Task RestartExplorerAsync()
    {
        await RunActionAsync(
            async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _backendClient.RestartExplorerAsync(cts.Token);
            },
            "RestartExplorer");
    }

    private static void OpenShellTarget(string target)
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private static bool ShowPropertiesDialog(string filePath)
    {
        var info = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            lpVerb = "Properties",
            lpFile = filePath,
            nShow = 5,
            fMask = 12
        };

        return ShellExecuteEx(ref info);
    }

    private void OpenRegistryEditor(string fullRegistryPath)
    {
        using var regeditKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit");
        var computerLabel = _localization.CurrentCultureName switch
        {
            "zh-CN" => "计算机",
            "zh-TW" => "電腦",
            _ => "Computer"
        };
        regeditKey?.SetValue("LastKey", $@"{computerLabel}\{fullRegistryPath}", RegistryValueKind.String);
        Process.Start(new ProcessStartInfo("regedit.exe", _settingsService.Current.OpenMoreRegedit ? "-m" : string.Empty)
        {
            UseShellExecute = true
        });
    }

    private Task ShowMissingTargetAsync(string messageKey, string titleKey)
    {
        return FrontendMessageBox.ShowInfoAsync(
            _localization.Translate(messageKey),
            _localization.Translate(titleKey));
    }

    private async Task RunActionAsync(Func<Task> action, string titleKey)
    {
        var startedAt = DateTimeOffset.UtcNow;
        FrontendDebugLog.Operation(
            "FrontendOperation",
            $"FrontendLocalActionStart: Action={titleKey}, Timestamp={startedAt:O}.");
        try
        {
            await action();
            FrontendDebugLog.Operation(
                "FrontendOperation",
                $"FrontendLocalActionEnd: Action={titleKey}, Success=true, ElapsedMs={(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds:F0}.");
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error(
                "FrontendOperation",
                ex,
                $"FrontendLocalActionEnd: Action={titleKey}, Success=false, ElapsedMs={(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds:F0}.");
            await FrontendMessageBox.ShowErrorAsync(
                _localization.Format("DetailsActionFailed", ex.Message),
                _localization.Translate(titleKey));
        }
    }

    private static string GetClassesRootPath(string relativeRegistryPath) => $@"HKEY_CLASSES_ROOT\{relativeRegistryPath}";

    private static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "context-menu-item" : cleaned;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public nint hwnd;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string? lpVerb;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string? lpFile;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string? lpParameters;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string? lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        [MarshalAs(UnmanagedType.LPTStr)]
        public string? lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIconOrMonitor;
        public nint hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo lpExecInfo);
}
