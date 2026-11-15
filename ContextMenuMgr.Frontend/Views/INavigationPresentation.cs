namespace ContextMenuMgr.Frontend.Views;

/// <summary>Defers costly list presentation until the page's actual transition has completed.</summary>
public interface INavigationPresentation
{
    void OnNavigationStarted();
    void OnNavigationCompleted();
    void OnNavigatingAway();
}
