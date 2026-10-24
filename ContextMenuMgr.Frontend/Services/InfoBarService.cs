using System.Windows;
using Wpf.Ui.Controls;

namespace ContextMenuMgr.Frontend.Services;

internal sealed class InfoBarService : IInfoBarService, IDisposable
{
    private InfoBar? _host;

    public void SetInfoBarControl(InfoBar host)
    {
        _host = host;
        host.Unloaded += OnUnloaded;
    }

    public void ShowInformationalInfoBar(string title, string message) => Show(title, message, null, null);
    public void ShowInformationalInfoBar(string title, string message, string linkText, string linkUrl)
        => Show(title, message, linkText, linkUrl);

    public void CloseInfoBar() => Dispatch(() => { if (_host is not null) _host.IsOpen = false; });

    private void Show(string title, string message, string? linkText, string? linkUrl)
        => Dispatch(() =>
        {
            if (_host is null) return;
            _host.Title = title;
            _host.Message = message;
            _host.Severity = InfoBarSeverity.Informational;
            _host.Content = string.IsNullOrWhiteSpace(linkUrl) ? null
                : new HyperlinkButton { Content = linkText, NavigateUri = linkUrl, Padding = new Thickness(0) };
            _host.IsOpen = true;
        });

    private void Dispatch(Action action)
    {
        if (_host is not { } host || host.Dispatcher.HasShutdownStarted) return;
        if (host.Dispatcher.CheckAccess()) action();
        else _ = host.Dispatcher.BeginInvoke(action);
    }
    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();
    public void Dispose()
    {
        if (_host is not null) _host.Unloaded -= OnUnloaded;
        _host = null;
    }
}
