using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ContextMenuMgr.Frontend.Views.Pages;
using Wpf.Ui.Appearance;

namespace ContextMenuMgr.Frontend;

/// <summary>
/// Represents the main Window.
/// </summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private Type? _pendingPageType;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow(
        ViewModels.ShellViewModel viewModel,
        IServiceProvider serviceProvider)
    {
        SystemThemeWatcher.Watch(this);
        InitializeComponent();
        RootNavigation.SetServiceProvider(serviceProvider);
        DataContext = viewModel;

        ApplyWindowIcon();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Navigates to to.
    /// </summary>
    public void NavigateTo(Type pageType)
    {
        _pendingPageType = pageType;
        if (IsLoaded)
        {
            RootNavigation.Navigate(pageType);
        }
    }

    /// <summary>
    /// Executes bring To Foreground.
    /// </summary>
    public void BringToForeground()
    {
        var previousTopmost = Topmost;
        Topmost = true;
        Activate();
        Topmost = previousTopmost;
        Focus();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var targetPageType = _pendingPageType ?? typeof(FileContextMenuPage);
        RootNavigation.Navigate(targetPageType);
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
