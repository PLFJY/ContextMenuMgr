using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Collections.ObjectModel;
using System.Security.Principal;
using ContextMenuMgr.Frontend.Services;
using Microsoft.Win32;

namespace ContextMenuMgr.Frontend;

public partial class DebugToolsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly UpdateCheckService _updateCheckService;
    private readonly LocalizationService _localization;
    private readonly IBackendClient _backendClient;
    private CancellationTokenSource? _previewSequence;
    private CancellationTokenSource? _openWithSequence;
    private const string DebugOpenWithPrefix = "ContextMenuMgrPlus.DebugOpenWith.";
    private const string DebugOpenWithOwner = "ContextMenuMgrPlus.DebugOpenWithOwner";

    // Debug-only external-installer simulation. Normal management still uses the backend.
    private static RegistryKey? OpenDebugApplications(bool create)
    {
        var sid = WindowsIdentity.GetCurrent().User;
        if (sid?.IsAccountSid() != true) throw new InvalidOperationException("An interactive account SID is required.");
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
        using var classes = users.OpenSubKey($@"{sid.Value}\Software\Classes", writable: true)
            ?? throw new InvalidOperationException("The current user's Classes hive is not loaded.");
        return create ? classes.CreateSubKey("Applications", writable: true) : classes.OpenSubKey("Applications", writable: true);
    }

    private async void OnCreateOpenWithDebugClick(object sender, RoutedEventArgs e)
    {
        if (_openWithSequence is not null) return;
        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _openWithSequence = operation;
        CreateOpenWithDebugButton.IsEnabled = CleanupOpenWithDebugButton.IsEnabled = false;
        var id = Guid.NewGuid().ToString("N");
        var name = DebugOpenWithPrefix + id + ".exe";
        string? createdPath = null;
        try
        {
            var baseline = await _backendClient.GetOpenWithPendingReviewsAsync(operation.Token).WaitAsync(TimeSpan.FromSeconds(20), operation.Token);
            if (!baseline.BaselineEstablished)
            {
                OpenWithDebugStatusText.Text = _localization.Translate("DebugOpenWithBaselineWaiting");
                return;
            }
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Notepad executable not found.", executable);
            string path;
            using (var applications = OpenDebugApplications(create: true)!)
            {
                using (var existing = applications.OpenSubKey(name))
                    if (existing is not null) throw new IOException("The debug registration already exists; it was preserved.");
                var displayName = "ContextMenuMgr Debug OpenWith " + id[..8];
                var command = $"\"{executable}\" \"%1\"";
                using (var app = applications.CreateSubKey(name, writable: true))
                {
                    app.SetValue(DebugOpenWithOwner, id, RegistryValueKind.String);
                    app.SetValue("FriendlyAppName", displayName, RegistryValueKind.String);
                    using var commandKey = app.CreateSubKey(@"shell\open\command", writable: true);
                    commandKey.SetValue(null, command, RegistryValueKind.String);
                }
                using var actual = applications.OpenSubKey(name);
                using var actualCommand = actual?.OpenSubKey(@"shell\open\command");
                if (actual?.GetValue(DebugOpenWithOwner) as string != id
                    || actual.GetValue("FriendlyAppName") as string != displayName || actualCommand?.GetValue(null) as string != command)
                    throw new IOException("The debug registration could not be verified. Inspect the physical key before retrying.");
                path = applications.Name + "\\" + name;
            }
            createdPath = path;
            FrontendDebugLog.Operation(nameof(DebugToolsWindow), $"DebugOpenWithCreated: Path={path}");
            OpenWithDebugStatusText.Text = _localization.Translate("DebugOpenWithCreated") + Environment.NewLine + path;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), operation.Token);
                var current = await _backendClient.GetOpenWithPendingReviewsAsync(operation.Token).WaitAsync(TimeSpan.FromSeconds(20), operation.Token);
                var review = current.Reviews.FirstOrDefault(r => r.RegistryPath.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (review?.Status == ContextMenuMgr.Contracts.OpenWithReviewStatus.IsolationFailed)
                {
                    OpenWithDebugStatusText.Text = _localization.Translate("OpenWithMonitorIsolationFailed") + Environment.NewLine + path;
                    break;
                }
                if (review is null || review.Status is not (ContextMenuMgr.Contracts.OpenWithReviewStatus.New
                    or ContextMenuMgr.Contracts.OpenWithReviewStatus.Changed or ContextMenuMgr.Contracts.OpenWithReviewStatus.ReRegistered)) continue;
                OpenWithDebugStatusText.Text = _localization.Translate("DebugOpenWithPendingConfirmed") + Environment.NewLine + path;
                FrontendDebugLog.Operation(nameof(DebugToolsWindow), $"DebugOpenWithPendingConfirmed: Path={path}");
                break;
            }
        }
        catch (OperationCanceledException) when (!IsVisible) { }
        catch (Exception ex)
        {
            FrontendDebugLog.Error(nameof(DebugToolsWindow), ex, $"DebugOpenWithCreateFailed: Name={name}");
            OpenWithDebugStatusText.Text = _localization.Translate(createdPath is null ? "DebugOpenWithFailed" : "DebugOpenWithPendingUnverified")
                + Environment.NewLine + (createdPath ?? name) + Environment.NewLine + ex.Message;
        }
        finally
        {
            _openWithSequence = null;
            CreateOpenWithDebugButton.IsEnabled = CleanupOpenWithDebugButton.IsEnabled = true;
        }
    }

    private void OnCleanupOpenWithDebugClick(object sender, RoutedEventArgs e)
    {
        var removed = 0;
        try
        {
            using var applications = OpenDebugApplications(create: false);
            foreach (var name in applications?.GetSubKeyNames() ?? [])
            {
                // Exact GUID names and matching ownership only; no .deleted siblings or other applications.
                if (name.Length != DebugOpenWithPrefix.Length + 32 + 4
                    || !name.StartsWith(DebugOpenWithPrefix, StringComparison.Ordinal) || !name.EndsWith(".exe", StringComparison.Ordinal)
                    || !Guid.TryParseExact(name[DebugOpenWithPrefix.Length..^4], "N", out var id)) continue;
                using (var app = applications!.OpenSubKey(name))
                    if (app?.GetValue(DebugOpenWithOwner) as string != id.ToString("N")) continue;
                applications!.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                using var remaining = applications.OpenSubKey(name);
                if (remaining is not null) throw new IOException("The debug registration is still present.");
                removed++;
                FrontendDebugLog.Operation(nameof(DebugToolsWindow), $"DebugOpenWithRemoved: Path={applications.Name}\\{name}");
            }
            OpenWithDebugStatusText.Text = string.Format(_localization.Translate("DebugOpenWithCleaned"), removed);
        }
        catch (Exception ex)
        {
            FrontendDebugLog.Error(nameof(DebugToolsWindow), ex, $"DebugOpenWithCleanupFailed: Removed={removed}");
            OpenWithDebugStatusText.Text = string.Format(_localization.Translate("DebugOpenWithCleaned"), removed)
                + Environment.NewLine + _localization.Translate("DebugOpenWithFailed") + Environment.NewLine + ex.Message;
        }
    }

    public ObservableCollection<MenuListPreviewItem> MenuListPreviewItems { get; } =
    [
        new("缓存项目：文件右键菜单", "已从本地缓存立即显示"),
        new("缓存项目：文件夹右键菜单", "无需等待扫描")
    ];

    public DebugToolsWindow(
        UpdateCheckService updateCheckService,
        LocalizationService localization, IBackendClient backendClient)
    {
        _updateCheckService = updateCheckService;
        _localization = localization;
        _backendClient = backendClient;

        InitializeComponent();
        DataContext = this;
        WindowChromeTitleBarFactory.Apply(this, 44);
        ApplyWindowIcon();
        RefreshLocalizedText();

        StateChanged += (_, _) => UpdateMaximizeButtonIcon();
        _localization.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => _localization.LanguageChanged -= OnLanguageChanged;
        Closed += (_, _) => _previewSequence?.Cancel();
        Closed += (_, _) => _openWithSequence?.Cancel();
    }

    private async void OnPlayMenuListPreviewClick(object sender, RoutedEventArgs e)
    {
        _previewSequence?.Cancel();
        _previewSequence?.Dispose();
        var sequence = _previewSequence = new CancellationTokenSource();
        PlayMenuListPreviewButton.IsEnabled = false;
        MenuListPreviewItems.Clear();
        MenuListPreviewItems.Add(new("缓存项目：文件右键菜单", "已从本地缓存立即显示"));
        MenuListPreviewItems.Add(new("缓存项目：文件夹右键菜单", "无需等待扫描"));
        try
        {
            await Task.Delay(900, sequence.Token);
            MenuListPreviewItems.Insert(1, new("新发现的菜单项", "扫描完成后差量插入"));
            await Task.Delay(1800, sequence.Token);
            MenuListPreviewItems.RemoveAt(1);
            await FrontendMessageBox.ShowInfoAsync("该菜单项已不存在，列表已更新。", "菜单项已移除");
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_previewSequence, sequence))
            {
                _previewSequence = null;
                sequence.Dispose();
                PlayMenuListPreviewButton.IsEnabled = true;
            }
        }
    }

    private void OnForceUpdatePromptClick(object sender, RoutedEventArgs e)
    {
        _updateCheckService.ShowDebugUpdatePrompt();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedText();
    }

    private void RefreshLocalizedText()
    {
        Title = _localization.Translate("DebugToolsTitle");
        DebugTitleText.Text = Title;
        UpdatePromptTitleText.Text = _localization.Translate("DebugUpdatePromptTitle");
        ForceUpdatePromptButton.Content = _localization.Translate("DebugForceUpdatePromptText");
        OpenWithDebugTitleText.Text = _localization.Translate("DebugOpenWithTitle");
        OpenWithDebugHelpText.Text = _localization.Translate("DebugOpenWithHelp");
        CreateOpenWithDebugButton.Content = _localization.Translate("DebugOpenWithCreate");
        CleanupOpenWithDebugButton.Content = _localization.Translate("DebugOpenWithCleanup");
    }

    private void ApplyWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            Icon = BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute));
        }
    }

    private void WindowIcon_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Right)
        {
            SystemCommands.ShowSystemMenu(this, PointToScreen(e.GetPosition(this)));
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void UpdateMaximizeButtonIcon()
    {
        MaximizeButton.Icon = WindowState == WindowState.Maximized
            ? new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.SquareMultiple24 }
            : new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Maximize24 };
    }
}

public sealed record MenuListPreviewItem(string Title, string Subtitle);
