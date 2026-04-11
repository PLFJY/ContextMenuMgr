using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ContextMenuMgr.Frontend.ViewModels;
using ContextMenuMgr.Frontend.Views.Pages;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ContextMenuMgr.Frontend;

public partial class MainWindow : FluentWindow
{
    private bool _allowClose;

    public MainWindow(
        ShellViewModel viewModel,
        IServiceProvider serviceProvider)
    {
        SystemThemeWatcher.Watch(this);
        InitializeComponent();
        RootNavigation.SetServiceProvider(serviceProvider);
        DataContext = viewModel;

        ApplyWindowIcon();

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FileNavigationItem.IsActive = true;
        RootNavigation.Navigate(typeof(FileContextMenuPage));
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            ((App)System.Windows.Application.Current).MinimizeToTray();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        ((App)System.Windows.Application.Current).BeginDestroyWindowToTray(this);
    }

    public void AllowCloseToTray()
    {
        _allowClose = true;
    }

    public void PrepareForAppShutdown()
    {
        _allowClose = true;
    }

    private void ApplyWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            Icon = BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute));
        }
    }
}
