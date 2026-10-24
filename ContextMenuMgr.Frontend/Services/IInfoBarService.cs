using Wpf.Ui.Controls;

namespace ContextMenuMgr.Frontend.Services;

public interface IInfoBarService
{
    void SetInfoBarControl(InfoBar host);
    void ShowInformationalInfoBar(string title, string message);
    void ShowInformationalInfoBar(string title, string message, string linkText, string linkUrl);
    void CloseInfoBar();
}
