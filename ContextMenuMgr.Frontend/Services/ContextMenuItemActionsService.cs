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
#if CONTEXTMENUMGR_IN_PROCESS
    private readonly PortableInteractiveUser? _portableUser;
#endif

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextMenuItemActionsService"/> class.
    /// </summary>
#if CONTEXTMENUMGR_IN_PROCESS
    internal ContextMenuItemActionsService(LocalizationService localization, FrontendSettingsService settingsService,
#else
    public ContextMenuItemActionsService(LocalizationService localization, FrontendSettingsService settingsService,
#endif
        IBackendClient backendClient
#if CONTEXTMENUMGR_IN_PROCESS
        , PortableInteractiveUser? portableUser = null
#endif
        )
    {
        _localization = localization;
        _settingsService = settingsService;
        _backendClient = backendClient;
#if CONTEXTMENUMGR_IN_PROCESS
        _portableUser = portableUser;
#endif
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
                var arguments = File.Exists(targetPath)
                    ? useSeparateWindow ? new[] { "/n,/select,", targetPath } : new[] { "/select,", targetPath }
                    : useSeparateWindow ? new[] { "/n,", parentDirectory ?? targetPath } : new[] { parentDirectory ?? targetPath };
#if CONTEXTMENUMGR_IN_PROCESS
                if (_portableUser is not null)
                {
                    StartForInteractiveUser(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                        BuildExplorerArguments(arguments), AppContext.BaseDirectory);
                    return;
                }
#endif
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

                OpenRegistryEditor(string.IsNullOrWhiteSpace(item.Entry.BackendRegistryPath)
                    ? GetClassesRootPath(item.Entry.RegistryPath)
                    : item.Entry.BackendRegistryPath);
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

            var registryPath = GetClassesRootPath(item.Entry.RegistryPath);
            var exportArguments = new[] { "export", registryPath, fileDialog.FileName, "/y" };
#if CONTEXTMENUMGR_IN_PROCESS
            using var process = StartForInteractiveUser(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"),
                exportArguments, (_portableUser ?? throw new InvalidOperationException("The verified desktop user's process token is unavailable.")).Context.ProfilePath);
#else
            using var process = Process.Start(new ProcessStartInfo("reg.exe", $"export \"{registryPath}\" \"{fileDialog.FileName}\" /y")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
#endif

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

    private void OpenShellTarget(string target)
    {
#if CONTEXTMENUMGR_IN_PROCESS
        if (_portableUser is not null)
        {
            StartForInteractiveUser(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                [target], _portableUser.Context.ProfilePath);
            return;
        }
#endif
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    public void OpenFolder(string path)
    {
#if CONTEXTMENUMGR_IN_PROCESS
        if (_portableUser is not null)
        {
            StartForInteractiveUser(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                [path], _portableUser.Context.ProfilePath);
            return;
        }
#endif
        Process.Start(new ProcessStartInfo("explorer.exe", QuoteForShell(path)) { UseShellExecute = true });
    }

    private bool ShowPropertiesDialog(string filePath)
    {
#if CONTEXTMENUMGR_IN_PROCESS
        if (_portableUser is not null)
            throw new InvalidOperationException("File Properties is unavailable in elevated Portable mode because it can activate third-party shell handlers in the elevated process.");
#endif
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
        // LastKey accepts a hive-qualified path. A localized computer prefix
        // based on the application's language may not match Windows' language.
#if CONTEXTMENUMGR_IN_PROCESS
        if (_portableUser is not null)
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
            using var userHive = users.OpenSubKey(_portableUser.Context.Sid, writable: true)
                ?? throw new InvalidOperationException("The verified desktop user's registry hive is no longer loaded.");
            using var regeditKey = userHive.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", writable: true)
                ?? throw new InvalidOperationException("Could not open the desktop user's Registry Editor settings.");
            regeditKey.SetValue("LastKey", fullRegistryPath, RegistryValueKind.String);
        }
#else
        using (var regeditKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
        {
            regeditKey.SetValue("LastKey", fullRegistryPath, RegistryValueKind.String);
        }
#endif

        // An existing instance does not reload LastKey when activated. Open a
        // fresh instance for navigation without closing the user's other windows.
        var existingEditors = Process.GetProcessesByName("regedit");
        bool hasExistingEditor;
        try
        {
            var sessionId = Process.GetCurrentProcess().SessionId;
            hasExistingEditor = existingEditors.Any(editor => editor.SessionId == sessionId);
        }
        finally
        {
            foreach (var editor in existingEditors)
            {
                editor.Dispose();
            }
        }

        var forceNewInstance = _settingsService.Current.OpenMoreRegedit || hasExistingEditor;
        FrontendDebugLog.Operation("FrontendOperation",
            $"RegistryEditorNavigationRequested: RegistryPath={fullRegistryPath}, NewInstance={forceNewInstance}.");
#if CONTEXTMENUMGR_IN_PROCESS
        if (_portableUser is not null)
        {
            StartForInteractiveUser(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe"),
                forceNewInstance ? ["-m"] : [], _portableUser.Context.ProfilePath);
            return;
        }
#endif
        using var process = Process.Start(new ProcessStartInfo("regedit.exe", forceNewInstance ? "-m" : string.Empty)
            { UseShellExecute = true }) ?? throw new InvalidOperationException("Failed to start regedit.exe.");
    }

#if CONTEXTMENUMGR_IN_PROCESS
    private Process StartForInteractiveUser(string executablePath, IEnumerable<string> arguments, string workingDirectory)
        => (_portableUser ?? throw new InvalidOperationException("The verified desktop user's process token is unavailable."))
            .StartNonElevatedProcess(executablePath, arguments, workingDirectory);

    private static string[] BuildExplorerArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 2 && arguments[0] is "/n,/select," or "/select,")
            return [arguments[0] + QuoteArgument(arguments[1])];
        if (arguments.Count == 2 && arguments[0] is "/n," or "/")
            return [arguments[0] + QuoteArgument(arguments[1])];
        return arguments.Select(QuoteArgument).ToArray();
    }

    private static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
#endif

    private static string QuoteForShell(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

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

    private static string GetClassesRootPath(string registryPath)
        => registryPath.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)
            ? registryPath
            : $@"HKEY_CLASSES_ROOT\{registryPath}";

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
