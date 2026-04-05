using CommunityToolkit.Mvvm.ComponentModel;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class NavigationItemViewModel : ObservableObject
{
    public NavigationItemViewModel(string title, string glyph, object page)
    {
        Title = title;
        Glyph = glyph;
        Page = page;
    }

    [ObservableProperty]
    public partial string Title { get; set; }

    public string Glyph { get; }

    public object Page { get; }

    [ObservableProperty]
    public partial string? BadgeText { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
