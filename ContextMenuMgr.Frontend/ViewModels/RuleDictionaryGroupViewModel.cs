using System.Collections.ObjectModel;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the rule Dictionary Group View Model.
/// </summary>
public sealed class RuleDictionaryGroupViewModel
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RuleDictionaryGroupViewModel"/> class.
    /// </summary>
    public RuleDictionaryGroupViewModel(string title, IEnumerable<string> items)
    {
        Title = title;
        Items = new ObservableCollection<string>(items);
    }

    /// <summary>
    /// Gets the title.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the items.
    /// </summary>
    public ObservableCollection<string> Items { get; }
}
