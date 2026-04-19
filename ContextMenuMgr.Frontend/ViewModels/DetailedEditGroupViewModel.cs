using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the detailed Edit Group View Model.
/// </summary>
public partial class DetailedEditGroupViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DetailedEditGroupViewModel"/> class.
    /// </summary>
    public DetailedEditGroupViewModel(
        DetailedEditGroupDefinition definition,
        DetailedEditRuleService ruleService,
        LocalizationService localization)
    {
        Title = definition.Title;
        RegistryPath = definition.RegistryPath;
        FilePath = definition.FilePath;
        IsIniGroup = definition.IsIniGroup;
        IsAvailable = definition.IsAvailable;
        Rules = definition.Rules
            .Select(rule => new DetailedEditRuleViewModel(rule, ruleService, localization))
            .ToArray();
    }

    /// <summary>
    /// Gets the title.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the registry Path.
    /// </summary>
    public string? RegistryPath { get; }

    /// <summary>
    /// Gets the file Path.
    /// </summary>
    public string? FilePath { get; }

    /// <summary>
    /// Gets a value indicating whether ini Group.
    /// </summary>
    public bool IsIniGroup { get; }

    /// <summary>
    /// Gets a value indicating whether available.
    /// </summary>
    public bool IsAvailable { get; }

    /// <summary>
    /// Gets the rules.
    /// </summary>
    public IReadOnlyList<DetailedEditRuleViewModel> Rules { get; }
}
