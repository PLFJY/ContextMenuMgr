using System.Collections.ObjectModel;

namespace ContextMenuMgr.Frontend.ViewModels;

public sealed class RuleDictionaryGroupViewModel
{
    public RuleDictionaryGroupViewModel(string title, IEnumerable<string> items)
    {
        Title = title;
        Items = new ObservableCollection<string>(items);
    }

    public string Title { get; }

    public ObservableCollection<string> Items { get; }
}
