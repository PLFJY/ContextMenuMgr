using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>Keeps localized presentation outside IPC and its settings/ACL dependency graph.</summary>
public sealed class MenuListFeedbackService : IDisposable
{
    private readonly IBackendClient _backend;
    private readonly LocalizationService _localization;

    public MenuListFeedbackService(IBackendClient backend, LocalizationService localization)
    {
        _backend = backend;
        _localization = localization;
        _backend.ItemsRemoved += OnItemsRemoved;
    }

    private void OnItemsRemoved(object? sender, MenuItemsRemovedEventArgs e)
    {
        // Classic toggle feedback also covers its timeout verification in the workspace.
        if (!e.NotifyUser || e.Request.Command == PipeCommand.SetEnabled) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        _ = dispatcher.InvokeAsync(() => FrontendMessageBox.ShowInfoAsync(
            _localization.Translate("MenuItemRemovedMessage"),
            _localization.Translate("MenuItemRemovedTitle")));
    }

    public void Dispose() => _backend.ItemsRemoved -= OnItemsRemoved;
}
