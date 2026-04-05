namespace ContextMenuMgr.Frontend.ViewModels;

public sealed class SceneOptionViewModel
{
    public SceneOptionViewModel(string value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }

    public string Value { get; }

    public string DisplayName { get; }
}
