using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the frontend Message Box.
/// </summary>
public static class FrontendMessageBox
{
    /// <summary>
    /// Shows info Async.
    /// </summary>
    public static async Task ShowInfoAsync(
        string message,
        string title,
        string? closeButtonText = null)
    {
        var localization = ResolveLocalization();
        var messageBox = new MessageBox
        {
            Title = title,
            Content = message,
            Owner = System.Windows.Application.Current?.MainWindow,
            CloseButtonText = closeButtonText ?? localization?.Translate("DialogClose") ?? "Close",
            CloseButtonIcon = new SymbolIcon(SymbolRegular.Dismiss24)
        };

        await messageBox.ShowDialogAsync();
    }

    /// <summary>
    /// Shows error Async.
    /// </summary>
    public static async Task ShowErrorAsync(
        string message,
        string title,
        string? closeButtonText = null)
    {
        var localization = ResolveLocalization();
        var messageBox = new MessageBox
        {
            Title = title,
            Content = message,
            Owner = System.Windows.Application.Current?.MainWindow,
            CloseButtonText = closeButtonText ?? localization?.Translate("DialogClose") ?? "Close",
            CloseButtonIcon = new SymbolIcon(SymbolRegular.Dismiss24)
        };

        await messageBox.ShowDialogAsync();
    }

    /// <summary>
    /// Shows confirm Async.
    /// </summary>
    public static async Task<bool> ShowConfirmAsync(
        string message,
        string title,
        string? primaryButtonText = null,
        string? closeButtonText = null)
    {
        var localization = ResolveLocalization();
        var messageBox = new MessageBox
        {
            Title = title,
            Content = message,
            Owner = System.Windows.Application.Current?.MainWindow,
            PrimaryButtonText = primaryButtonText ?? localization?.Translate("DialogConfirm") ?? "Confirm",
            PrimaryButtonIcon = new SymbolIcon(SymbolRegular.Checkmark24),
            CloseButtonText = closeButtonText ?? localization?.Translate("DialogCancel") ?? "Cancel",
            CloseButtonIcon = new SymbolIcon(SymbolRegular.Dismiss24)
        };

        return await messageBox.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    private static LocalizationService? ResolveLocalization()
    {
        return System.Windows.Application.Current is App app
            ? app.TryGetService<LocalizationService>()
            : null;
    }
}
