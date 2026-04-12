using CommunityToolkit.Mvvm.ComponentModel;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class FileTypesPageViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization;

    public FileTypesPageViewModel(
        IBackendClient backendClient,
        ContextMenuWorkspaceService workspace,
        LocalizationService localization,
        IconPreviewService iconPreviewService,
        ContextMenuItemActionsService actionsService,
        FrontendSettingsService settingsService)
    {
        _localization = localization;

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
            fixedScopeValue: "lnkfile");

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
            fixedScopeValue: "Launcher.ImmersiveApplication");

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
            fixedScopeValue: "exefile");

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
            fixedScopeValue: "Unknown");

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
            settingsService);
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
            settingsService);
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
            settingsService);
        DirectoryTypeTab.ConfigureOptionSelector(
            localization.Translate("SceneDirectoryTypeSelectorLabel"),
            CreateDirectoryTypeOptions(localization),
            "Document");

        _localization.LanguageChanged += OnLanguageChanged;
    }

    public SceneContextMenuTabViewModel ShortcutTab { get; }

    public SceneContextMenuTabViewModel UwpShortcutTab { get; }

    public SceneContextMenuTabViewModel ExecutableTab { get; }

    public SceneContextMenuTabViewModel CustomExtensionTab { get; }

    public SceneContextMenuTabViewModel PerceivedTypeTab { get; }

    public SceneContextMenuTabViewModel DirectoryTypeTab { get; }

    public SceneContextMenuTabViewModel UnknownTypeTab { get; }

    public string Title => _localization.Translate("FileTypesPageTitle");

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

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        ShortcutTab.Dispose();
        UwpShortcutTab.Dispose();
        ExecutableTab.Dispose();
        CustomExtensionTab.Dispose();
        PerceivedTypeTab.Dispose();
        DirectoryTypeTab.Dispose();
        UnknownTypeTab.Dispose();
    }
}
