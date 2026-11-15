using ContextMenuMgr.Frontend.Controls.Modern.Frame;

namespace ContextMenuMgr.Frontend.Views;

/// <summary>Restores a self-scrolling page after its content has loaded.</summary>
public interface INavigationScrollOwner
{
    void OnNavigatingAway();
    void OnNavigated(ModernFrameNavigationMode mode);
}
