using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.ViewModels;
using Microsoft.Win32;

namespace ContextMenuMgr.Frontend.Services;

public sealed class ContextMenuItemActionsService
{
    private readonly LocalizationService _localization;
    private readonly FrontendSettingsService _settingsService;

    public ContextMenuItemActionsService(LocalizationService localization, FrontendSettingsService settingsService)
    {
        _localization = localization;
        _settingsService = settingsService;
    }

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

            var fileDialog = new Microsoft.Win32.SaveFileDialog
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

    public Task RestartExplorerAsync()
    {
        return RunActionAsync(
            async () =>
            {
                foreach (var explorerProcess in Process.GetProcessesByName("explorer"))
                {
                    try
                    {
                        explorerProcess.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        explorerProcess.Dispose();
                    }
                }

                await Task.Delay(800);

                var explorerPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "explorer.exe");

                Process.Start(new ProcessStartInfo(explorerPath)
                {
                    UseShellExecute = true
                });
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
        var computerLabel = _localization.UsesChinese()
            ? "计算机"
            : "Computer";
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
        try
        {
            await action();
        }
        catch (Exception ex)
        {
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
