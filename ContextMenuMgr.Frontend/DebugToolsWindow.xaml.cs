using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend;

public partial class DebugToolsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly UpdateCheckService _updateCheckService;
    private readonly LocalizationService _localization;

    public DebugToolsWindow(
        UpdateCheckService updateCheckService,
        LocalizationService localization)
    {
        _updateCheckService = updateCheckService;
        _localization = localization;

        InitializeComponent();
        WindowChromeTitleBarFactory.Apply(this, 44);
        ApplyWindowIcon();
        RefreshLocalizedText();

        StateChanged += (_, _) => UpdateMaximizeButtonIcon();
        _localization.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => _localization.LanguageChanged -= OnLanguageChanged;
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
