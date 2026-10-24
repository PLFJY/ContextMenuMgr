using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Collections.ObjectModel;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend;

public partial class DebugToolsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly UpdateCheckService _updateCheckService;
    private readonly LocalizationService _localization;
    private CancellationTokenSource? _previewSequence;

    public ObservableCollection<MenuListPreviewItem> MenuListPreviewItems { get; } =
    [
        new("缓存项目：文件右键菜单", "已从本地缓存立即显示"),
        new("缓存项目：文件夹右键菜单", "无需等待扫描")
    ];

    public DebugToolsWindow(
        UpdateCheckService updateCheckService,
        LocalizationService localization)
    {
        _updateCheckService = updateCheckService;
        _localization = localization;

        InitializeComponent();
        DataContext = this;
        WindowChromeTitleBarFactory.Apply(this, 44);
        ApplyWindowIcon();
        RefreshLocalizedText();

        StateChanged += (_, _) => UpdateMaximizeButtonIcon();
        _localization.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => _localization.LanguageChanged -= OnLanguageChanged;
        Closed += (_, _) => _previewSequence?.Cancel();
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
