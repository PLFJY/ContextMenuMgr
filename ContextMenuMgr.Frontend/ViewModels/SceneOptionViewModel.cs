namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the scene Option View Model.
/// </summary>
public sealed class SceneOptionViewModel
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SceneOptionViewModel"/> class.
    /// </summary>
    public SceneOptionViewModel(string value, string displayName)
    {
        Value = value;
        DisplayName = displayName;
    }

    /// <summary>
    /// Gets the value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the display Name.
    /// </summary>
    public string DisplayName { get; }
}
