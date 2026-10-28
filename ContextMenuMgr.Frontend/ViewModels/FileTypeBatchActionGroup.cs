using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class FileTypeBatchActionGroup : ObservableObject
{
    public FileTypeBatchActionGroup(string identity, string name, FileTypeBatchMatchKind matchKind,
        IReadOnlyList<FileTypeBatchRegistration> registrations, string evidence)
    {
        Identity = identity;
        Name = name;
        MatchKind = matchKind;
        Registrations = registrations;
        Evidence = evidence;
    }

    public string Identity { get; }
    public string Name { get; }
    public string Evidence { get; }
    public FileTypeBatchMatchKind MatchKind { get; }
    public IReadOnlyList<FileTypeBatchRegistration> Registrations { get; }
    public int Count => Registrations.Count;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public partial class FileTypeBatchRegistration : ObservableObject
{
    public FileTypeBatchRegistration(ContextMenuItemViewModel item, bool included, string evidence)
    {
        Item = item;
        IsIncluded = included;
        Evidence = evidence;
    }

    public ContextMenuItemViewModel Item { get; }

    public string Evidence { get; }

    [ObservableProperty]
    public partial bool IsIncluded { get; set; }
}
