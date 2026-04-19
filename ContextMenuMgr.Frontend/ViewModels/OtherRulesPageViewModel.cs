using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the other Rules Page View Model.
/// </summary>
public partial class OtherRulesPageViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization;
    private readonly RuleDictionaryCatalogService _ruleCatalogService;
    private readonly ContextMenuWorkspaceService _workspace;

    /// <summary>
    /// Initializes a new instance of the <see cref="OtherRulesPageViewModel"/> class.
    /// </summary>
    public OtherRulesPageViewModel(
        IBackendClient backendClient,
        ContextMenuWorkspaceService workspace,
        LocalizationService localization,
        IconPreviewService iconPreviewService,
        ContextMenuItemActionsService actionsService,
        RuleDictionaryCatalogService ruleCatalogService,
        EnhanceMenuRuleService enhanceMenuRuleService,
        FrontendSettingsService settingsService,
        DetailedEditRuleService detailedEditRuleService)
    {
        _workspace = workspace;
        _localization = localization;
        _ruleCatalogService = ruleCatalogService;

        CustomRegistryPathTab = new SceneContextMenuTabViewModel(
            "DatabaseSearch24",
            localization.Translate("SceneCustomRegistryTitle"),
            localization.Translate("SceneCustomRegistryDescription"),
            ContextMenuSceneKind.CustomRegistryPath,
            backendClient,
            workspace,
            localization,
            iconPreviewService,
            actionsService,
            settingsService);
        CustomRegistryPathTab.ConfigureTextSelector(
            localization.Translate("SceneRegistryPathSelectorLabel"),
            @"HKCR\*\shell");
        _ = CustomRegistryPathTab.RefreshAsync();

        _workspace.Items.CollectionChanged += OnWorkspaceItemsCollectionChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged += OnWorkspaceItemPropertyChanged;
        }

        RefreshDefinitions(detailedEditRuleService, enhanceMenuRuleService, iconPreviewService);
        _localization.LanguageChanged += (_, _) =>
        {
            RefreshDefinitions(detailedEditRuleService, enhanceMenuRuleService, iconPreviewService);
            CustomRegistryPathTab.Title = _localization.Translate("SceneCustomRegistryTitle");
            CustomRegistryPathTab.Description = _localization.Translate("SceneCustomRegistryDescription");
            CustomRegistryPathTab.SelectorLabel = _localization.Translate("SceneRegistryPathSelectorLabel");
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(EnhanceMenusTitle));
            OnPropertyChanged(nameof(DetailedEditTitle));
            OnPropertyChanged(nameof(NoSelectionText));
        };
    }

    /// <summary>
    /// Gets the custom Registry Path Tab.
    /// </summary>
    public SceneContextMenuTabViewModel CustomRegistryPathTab { get; }

    /// <summary>
    /// Gets or sets the enhance Groups.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<EnhanceMenuGroupViewModel> EnhanceGroups { get; private set; } = [];

    /// <summary>
    /// Gets or sets the selected Enhance Group.
    /// </summary>
    [ObservableProperty]
    public partial EnhanceMenuGroupViewModel? SelectedEnhanceGroup { get; set; }

    /// <summary>
    /// Gets or sets the detailed Edit Groups.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<DetailedEditGroupViewModel> DetailedEditGroups { get; private set; } = [];

    /// <summary>
    /// Gets or sets the selected Detailed Edit Group.
    /// </summary>
    [ObservableProperty]
    public partial DetailedEditGroupViewModel? SelectedDetailedEditGroup { get; set; }

    public string Title => _localization.Translate("OtherRulesPageTitle");

    public string EnhanceMenusTitle => _localization.Translate("EnhanceMenusTitle");

    public string DetailedEditTitle => _localization.Translate("DetailedEditTitle");

    public string NoSelectionText => _localization.Translate("NoRuleGroupSelected");

    partial void OnSelectedEnhanceGroupChanged(EnhanceMenuGroupViewModel? value) { }

    private void RefreshDefinitions(
        DetailedEditRuleService detailedEditRuleService,
        EnhanceMenuRuleService enhanceMenuRuleService,
        IconPreviewService iconPreviewService)
    {
        EnhanceGroups = _ruleCatalogService
            .LoadEnhanceMenuGroups()
            .Select(definition => new EnhanceMenuGroupViewModel(
                definition,
                _localization,
                enhanceMenuRuleService,
                iconPreviewService,
                RefreshWorkspaceAndEnhanceStatesAsync))
            .ToArray();

        SelectedEnhanceGroup = EnhanceGroups.FirstOrDefault();

        DetailedEditGroups = _ruleCatalogService
            .LoadDetailedEditGroups()
            .Select(definition => new DetailedEditGroupViewModel(definition, detailedEditRuleService, _localization))
            .Where(static group => group.IsAvailable)
            .ToArray();

        SelectedDetailedEditGroup = DetailedEditGroups.FirstOrDefault();
    }

    private void RefreshEnhanceStates()
    {
        foreach (var group in EnhanceGroups)
        {
            group.RefreshStates();
        }
    }

    private async Task RefreshWorkspaceAndEnhanceStatesAsync()
    {
        await _workspace.RefreshAsync();
        RefreshEnhanceStates();
    }

    private void OnWorkspaceItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ContextMenuItemViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnWorkspaceItemPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (ContextMenuItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnWorkspaceItemPropertyChanged;
            }
        }

        RefreshEnhanceStates();
    }

    private void OnWorkspaceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ContextMenuItemViewModel.IsEnabled)
            or nameof(ContextMenuItemViewModel.IsDeleted)
            or nameof(ContextMenuItemViewModel.IsPresentInRegistry))
        {
            RefreshEnhanceStates();
        }
    }

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        _workspace.Items.CollectionChanged -= OnWorkspaceItemsCollectionChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged -= OnWorkspaceItemPropertyChanged;
        }

        foreach (var group in EnhanceGroups)
        {
            group.Dispose();
        }

        CustomRegistryPathTab.Dispose();
    }
}
