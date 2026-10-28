using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the file Types Page View Model.
/// </summary>
public partial class FileTypesPageViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization;
    private readonly IBackendClient _backendClient;
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly IconPreviewService _iconPreviewService;
    private readonly ContextMenuItemActionsService _actionsService;
    private readonly FrontendSettingsService _settingsService;
    private readonly ContextMenuApplicationIdentityService _identityService;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTypesPageViewModel"/> class.
    /// </summary>
    public FileTypesPageViewModel(
        IBackendClient backendClient,
        ContextMenuWorkspaceService workspace,
        LocalizationService localization,
        IconPreviewService iconPreviewService,
        ContextMenuItemActionsService actionsService,
        FrontendSettingsService settingsService,
        ContextMenuApplicationIdentityService identityService)
    {
        _localization = localization;
        _backendClient = backendClient;
        _workspace = workspace;
        _iconPreviewService = iconPreviewService;
        _actionsService = actionsService;
        _settingsService = settingsService;
        _identityService = identityService;

        ShortcutTab = new SceneContextMenuTabViewModel(
            "Link24",
            localization.Translate("SceneLnkFileTitle"),
            localization.Translate("SceneLnkFileDescription"),
            ContextMenuSceneKind.LnkFile,
            backendClient,
            workspace,
            localization,
            iconPreviewService,
            actionsService,
            settingsService,
            fixedScopeValue: "lnkfile",
            identityService: identityService,
            openBatchManagementAsync: OpenBatchManagementAsync);

        UwpShortcutTab = new SceneContextMenuTabViewModel(
            "AppsList24",
            localization.Translate("SceneUwpShortcutTitle"),
            localization.Translate("SceneUwpShortcutDescription"),
            ContextMenuSceneKind.UwpShortcut,
            backendClient,
            workspace,
            localization,
            iconPreviewService,
            actionsService,
            settingsService,
            fixedScopeValue: "Launcher.ImmersiveApplication",
            identityService: identityService,
            openBatchManagementAsync: OpenBatchManagementAsync);

        ExecutableTab = new SceneContextMenuTabViewModel(
            "WindowDevTools24",
            localization.Translate("SceneExeFileTitle"),
            localization.Translate("SceneExeFileDescription"),
            ContextMenuSceneKind.ExeFile,
            backendClient,
            workspace,
            localization,
            iconPreviewService,
            actionsService,
            settingsService,
            fixedScopeValue: "exefile",
            identityService: identityService,
            openBatchManagementAsync: OpenBatchManagementAsync);

        UnknownTypeTab = new SceneContextMenuTabViewModel(
            "DocumentQuestionMark24",
            localization.Translate("SceneUnknownTypeTitle"),
            localization.Translate("SceneUnknownTypeDescription"),
            ContextMenuSceneKind.UnknownType,
            backendClient,
            workspace,
            localization,
            iconPreviewService,
            actionsService,
            settingsService,
            fixedScopeValue: "Unknown",
            identityService: identityService,
            openBatchManagementAsync: OpenBatchManagementAsync);

        CustomExtensionTab = new SceneContextMenuTabViewModel(
            "DocumentText24",
            localization.Translate("SceneCustomExtensionTitle"),
            localization.Translate("SceneCustomExtensionDescription"),
            ContextMenuSceneKind.CustomExtension,
            backendClient,
            workspace,
            localization,
            iconPreviewService,
            actionsService,
            settingsService,
            identityService: identityService,
            openBatchManagementAsync: OpenBatchManagementAsync);
        CustomExtensionTab.ConfigureTextSelector(
            localization.Translate("SceneExtensionSelectorLabel"),
            ".txt");
        _ = CustomExtensionTab.RefreshAsync();

        PerceivedTypeTab = new SceneContextMenuTabViewModel(
            "TextGrammarArrowLeft24",
            localization.Translate("ScenePerceivedTypeTitle"),
            localization.Translate("ScenePerceivedTypeDescription"),
            ContextMenuSceneKind.PerceivedType,
            backendClient,
            workspace,
            localization,
            iconPreviewService,
            actionsService,
            settingsService,
            identityService: identityService,
            openBatchManagementAsync: OpenBatchManagementAsync);
        PerceivedTypeTab.ConfigureOptionSelector(
            localization.Translate("ScenePerceivedTypeSelectorLabel"),
            CreatePerceivedTypeOptions(localization),
            "Text");

        DirectoryTypeTab = new SceneContextMenuTabViewModel(
            "FolderArrowRight24",
            localization.Translate("SceneDirectoryTypeTitle"),
            localization.Translate("SceneDirectoryTypeDescription"),
            ContextMenuSceneKind.DirectoryType,
            backendClient,
            workspace,
            localization,
            iconPreviewService,
            actionsService,
            settingsService,
            identityService: identityService,
            openBatchManagementAsync: OpenBatchManagementAsync);
        DirectoryTypeTab.ConfigureOptionSelector(
            localization.Translate("SceneDirectoryTypeSelectorLabel"),
            CreateDirectoryTypeOptions(localization),
            "Document");

        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Gets the shortcut Tab.
    /// </summary>
    public SceneContextMenuTabViewModel ShortcutTab { get; }

    /// <summary>
    /// Gets the uwp Shortcut Tab.
    /// </summary>
    public SceneContextMenuTabViewModel UwpShortcutTab { get; }

    /// <summary>
    /// Gets the executable Tab.
    /// </summary>
    public SceneContextMenuTabViewModel ExecutableTab { get; }

    /// <summary>
    /// Gets the custom Extension Tab.
    /// </summary>
    public SceneContextMenuTabViewModel CustomExtensionTab { get; }

    /// <summary>
    /// Gets the perceived Type Tab.
    /// </summary>
    public SceneContextMenuTabViewModel PerceivedTypeTab { get; }

    /// <summary>
    /// Gets the directory Type Tab.
    /// </summary>
    public SceneContextMenuTabViewModel DirectoryTypeTab { get; }

    /// <summary>
    /// Gets the unknown Type Tab.
    /// </summary>
    public SceneContextMenuTabViewModel UnknownTypeTab { get; }

    public string Title => _localization.Translate("FileTypesPageTitle");

    [ObservableProperty]
    private int _selectedTabIndex = -1;

    [ObservableProperty]
    private bool _isBatchManagementActive;

    [ObservableProperty]
    private FileTypeBatchManagementViewModel? _batchManagement;

    [RelayCommand]
    private async Task OpenBatchManagementAsync(ContextMenuItemViewModel? sourceItem)
    {
        if (sourceItem is null || !_identityService.CanBatchManageFileTypeItem(sourceItem))
        {
            return;
        }

        BatchManagement?.Dispose();
        var query = _identityService.CreateFileTypeBatchQuery(sourceItem);
        BatchManagement = new FileTypeBatchManagementViewModel(
            sourceItem,
            query,
            _backendClient,
            _workspace,
            _localization,
            _iconPreviewService,
            _actionsService,
            BackFromBatchManagementAsync);
        IsBatchManagementActive = true;
        await BatchManagement.RefreshAsync();
    }

    [RelayCommand]
    private Task BackFromBatchManagementAsync()
    {
        IsBatchManagementActive = false;
        return Task.CompletedTask;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ShortcutTab.Title = _localization.Translate("SceneLnkFileTitle");
        ShortcutTab.Description = _localization.Translate("SceneLnkFileDescription");
        UwpShortcutTab.Title = _localization.Translate("SceneUwpShortcutTitle");
        UwpShortcutTab.Description = _localization.Translate("SceneUwpShortcutDescription");
        ExecutableTab.Title = _localization.Translate("SceneExeFileTitle");
        ExecutableTab.Description = _localization.Translate("SceneExeFileDescription");
        CustomExtensionTab.Title = _localization.Translate("SceneCustomExtensionTitle");
        CustomExtensionTab.Description = _localization.Translate("SceneCustomExtensionDescription");
        CustomExtensionTab.SelectorLabel = _localization.Translate("SceneExtensionSelectorLabel");
        PerceivedTypeTab.Title = _localization.Translate("ScenePerceivedTypeTitle");
        PerceivedTypeTab.Description = _localization.Translate("ScenePerceivedTypeDescription");
        PerceivedTypeTab.SelectorLabel = _localization.Translate("ScenePerceivedTypeSelectorLabel");
        PerceivedTypeTab.ConfigureOptionSelector(
            _localization.Translate("ScenePerceivedTypeSelectorLabel"),
            CreatePerceivedTypeOptions(_localization),
            PerceivedTypeTab.SelectedOption?.Value);
        DirectoryTypeTab.Title = _localization.Translate("SceneDirectoryTypeTitle");
        DirectoryTypeTab.Description = _localization.Translate("SceneDirectoryTypeDescription");
        DirectoryTypeTab.ConfigureOptionSelector(
            _localization.Translate("SceneDirectoryTypeSelectorLabel"),
            CreateDirectoryTypeOptions(_localization),
            DirectoryTypeTab.SelectedOption?.Value);
        UnknownTypeTab.Title = _localization.Translate("SceneUnknownTypeTitle");
        UnknownTypeTab.Description = _localization.Translate("SceneUnknownTypeDescription");
        OnPropertyChanged(nameof(Title));
    }

    public Task RefreshAsync() => IsBatchManagementActive && BatchManagement is not null
        ? BatchManagement.RefreshAsync()
        : (SelectedTabIndex switch {
            1 => UwpShortcutTab, 2 => ExecutableTab, 3 => CustomExtensionTab,
            4 => PerceivedTypeTab, 5 => DirectoryTypeTab, 6 => UnknownTypeTab, _ => ShortcutTab }).RefreshAsync();

    public async Task ApplyAnalysisResultAsync(FileTypeAnalysisResult result)
    {
        switch (result.SceneKind)
        {
            case ContextMenuSceneKind.LnkFile:
                ShortcutTab.ScopeValue = "lnkfile";
                SelectedTabIndex = 0;
                await ShortcutTab.RefreshAsync();
                break;
            case ContextMenuSceneKind.UwpShortcut:
                UwpShortcutTab.ScopeValue = "Launcher.ImmersiveApplication";
                SelectedTabIndex = 1;
                await UwpShortcutTab.RefreshAsync();
                break;
            case ContextMenuSceneKind.ExeFile:
                ExecutableTab.ScopeValue = "exefile";
                SelectedTabIndex = 2;
                await ExecutableTab.RefreshAsync();
                break;
            case ContextMenuSceneKind.CustomExtension:
                CustomExtensionTab.ScopeValue = result.ScopeValue ?? string.Empty;
                SelectedTabIndex = 3;
                await CustomExtensionTab.RefreshAsync();
                break;
            case ContextMenuSceneKind.PerceivedType:
                PerceivedTypeTab.ScopeValue = result.ScopeValue ?? string.Empty;
                SelectedTabIndex = 4;
                await PerceivedTypeTab.RefreshAsync();
                break;
            case ContextMenuSceneKind.DirectoryType:
                DirectoryTypeTab.ScopeValue = result.ScopeValue ?? "Document";
                SelectedTabIndex = 5;
                await DirectoryTypeTab.RefreshAsync();
                break;
            case ContextMenuSceneKind.UnknownType:
                UnknownTypeTab.ScopeValue = "Unknown";
                SelectedTabIndex = 6;
                await UnknownTypeTab.RefreshAsync();
                break;
            default:
                await FrontendMessageBox.ShowErrorAsync(
                    _localization.Format("UnsupportedSceneKind", result.SceneKind),
                    _localization.Translate("MenuAnalysisTitle"));
                break;
        }
    }

    private static IReadOnlyList<SceneOptionViewModel> CreatePerceivedTypeOptions(LocalizationService localization)
    {
        return
        [
            new("Text", localization.Translate("PerceivedTypeText")),
            new("Document", localization.Translate("PerceivedTypeDocument")),
            new("Image", localization.Translate("PerceivedTypeImage")),
            new("Video", localization.Translate("PerceivedTypeVideo")),
            new("Audio", localization.Translate("PerceivedTypeAudio")),
            new("Compressed", localization.Translate("PerceivedTypeCompressed")),
            new("System", localization.Translate("PerceivedTypeSystem"))
        ];
    }

    private static IReadOnlyList<SceneOptionViewModel> CreateDirectoryTypeOptions(LocalizationService localization)
    {
        return
        [
            new("Document", localization.Translate("DirectoryTypeDocument")),
            new("Image", localization.Translate("DirectoryTypeImage")),
            new("Video", localization.Translate("DirectoryTypeVideo")),
            new("Audio", localization.Translate("DirectoryTypeAudio"))
        ];
    }

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        BatchManagement?.Dispose();
        ShortcutTab.Dispose();
        UwpShortcutTab.Dispose();
        ExecutableTab.Dispose();
        CustomExtensionTab.Dispose();
        PerceivedTypeTab.Dispose();
        DirectoryTypeTab.Dispose();
        UnknownTypeTab.Dispose();
    }
}
