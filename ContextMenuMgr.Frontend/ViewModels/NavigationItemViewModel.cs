using CommunityToolkit.Mvvm.ComponentModel;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the navigation Item View Model.
/// </summary>
public partial class NavigationItemViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NavigationItemViewModel"/> class.
    /// </summary>
    public NavigationItemViewModel(string title, string glyph, object page)
    {
        Title = title;
        Glyph = glyph;
        Page = page;
    }

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>
    /// Gets the glyph.
    /// </summary>
    public string Glyph { get; }

    /// <summary>
    /// Gets the page.
    /// </summary>
    public object Page { get; }

    /// <summary>
    /// Gets or sets the badge Text.
    /// </summary>
    [ObservableProperty]
    public partial string? BadgeText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether selected.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
