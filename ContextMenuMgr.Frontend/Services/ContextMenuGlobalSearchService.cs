using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.ViewModels;
using ContextMenuMgr.Frontend.Views.Pages;
using Wpf.Ui.Controls;

namespace ContextMenuMgr.Frontend.Services;

public sealed class ContextMenuGlobalSearchService : IDisposable
{
    private const int DefaultResultLimit = 12;
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly Windows11ContextMenuService _windows11Service;
    private readonly LocalizationService _localization;
    private readonly IconPreviewService _iconPreviewService;
    private readonly FrontendSettingsService _settingsService;
    private readonly object _gate = new();
    private List<Candidate> _candidates = [];
    private bool _disposed;

    public ContextMenuGlobalSearchService(
        ContextMenuWorkspaceService workspace,
        Windows11ContextMenuService windows11Service,
        LocalizationService localization,
        IconPreviewService iconPreviewService,
        FrontendSettingsService settingsService)
    {
        _workspace = workspace;
        _windows11Service = windows11Service;
        _localization = localization;
        _iconPreviewService = iconPreviewService;
        _settingsService = settingsService;

        _workspace.Items.CollectionChanged += OnWorkspaceItemsChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged += OnWorkspaceItemPropertyChanged;
        }

        _windows11Service.ItemsChanged += OnWindows11ItemsChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;

        RebuildCandidates();
    }

    public event EventHandler? CandidatesChanged;

    public IReadOnlyList<GlobalSearchResultViewModel> Search(string? query, int limit = DefaultResultLimit)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        List<Candidate> snapshot;
        lock (_gate)
        {
            snapshot = _candidates;
        }

        return snapshot
            .Select(candidate => new ScoredCandidate(candidate, Score(candidate, query)))
            .Where(static candidate => candidate.Score > 0)
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Candidate.Result.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(static candidate => candidate.Candidate.Result)
            .ToList();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _workspace.Items.CollectionChanged -= OnWorkspaceItemsChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged -= OnWorkspaceItemPropertyChanged;
        }

        _windows11Service.ItemsChanged -= OnWindows11ItemsChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
    }

    private static int Score(Candidate candidate, string query)
    {
        return candidate.Scope == GlobalSearchScope.Windows11
            ? ScoreWindows11(candidate, query)
            : ScoreClassic(candidate, query);
    }

    private static int ScoreClassic(Candidate candidate, string query)
    {
        var score = ContextMenuSearchMatcher.TryScoreClassicEntry(
            candidate.Entry!,
            candidate.Result.ScopeLabel,
            candidate.Result.StateLabel,
            candidate.UserNote,
            query,
            out var matchedScore)
            ? matchedScore
            : 0;
        return score;
    }

    private static int ScoreWindows11(Candidate candidate, string query)
    {
        return ContextMenuSearchMatcher.TryScoreWindows11Definitions(
            candidate.Windows11Definitions!,
            candidate.Result.DisplayName,
            candidate.PackageFamilyName,
            candidate.PublisherName,
            candidate.ContextTypesText,
            candidate.Result.FilePath,
            candidate.Result.StateLabel,
            candidate.UserNote,
            query,
            out var score)
            ? score
            : 0;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RebuildCandidates();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        RebuildCandidates();
    }

    private void OnWindows11ItemsChanged(object? sender, EventArgs e)
    {
        RebuildCandidates();
    }

    private void OnWorkspaceItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
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

        RebuildCandidates();
    }

    private void OnWorkspaceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ContextMenuItemViewModel.DisplayName)
            or nameof(ContextMenuItemViewModel.IsEnabled)
            or nameof(ContextMenuItemViewModel.IsDeleted)
            or nameof(ContextMenuItemViewModel.IsWindows11ContextMenu)
            or nameof(ContextMenuItemViewModel.IconSource)
            or nameof(ContextMenuItemViewModel.UserNote))
        {
            RebuildCandidates();
        }
    }

    private void RebuildCandidates()
    {
        var candidates = new List<Candidate>();
        candidates.AddRange(_workspace.Items
            .Where(IsClassicGlobalSearchItem)
            .Select(CreateClassicCandidate));

        if (_windows11Service.IsSupported)
        {
            candidates.AddRange(CreateWindows11Candidates(_windows11Service.CurrentItems));
        }

        lock (_gate)
        {
            _candidates = candidates;
        }

        CandidatesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsClassicGlobalSearchItem(ContextMenuItemViewModel item)
    {
        return !item.IsWindows11ContextMenu
               && item.Category is ContextMenuCategory.File
                   or ContextMenuCategory.AllFileSystemObjects
                   or ContextMenuCategory.Folder
                   or ContextMenuCategory.Directory
                   or ContextMenuCategory.DirectoryBackground
                   or ContextMenuCategory.DesktopBackground
                   or ContextMenuCategory.Drive
                   or ContextMenuCategory.Library
                   or ContextMenuCategory.Computer
                   or ContextMenuCategory.RecycleBin;
    }

    private Candidate CreateClassicCandidate(ContextMenuItemViewModel item)
    {
        var categoryName = ContextMenuCategoryText.GetLocalizedName(item.Category, _localization);
        var result = new GlobalSearchResultViewModel
        {
            Id = item.Id,
            DisplayName = item.DisplayName,
            StateLabel = item.StateLabel,
            IsEnabled = item.IsEnabled,
            ScopeLabel = categoryName,
            Scope = GlobalSearchScope.Classic,
            Category = item.Category,
            IsWindows11 = false,
            TargetPageType = GetTargetPageType(item.Category),
            TargetFilterText = item.DisplayName,
            RegistryPath = item.Entry.RegistryPath,
            CommandText = item.Entry.CommandText,
            FilePath = item.Entry.FilePath,
            HandlerClsid = item.Entry.HandlerClsid,
            IconSource = TryGetClassicIcon(item),
            FallbackIconSymbol = GetFallbackIconSymbol(item.Category),
            JumpText = _localization.Translate("GlobalSearchJump"),
            SearchBlob = ContextMenuSearchMatcher.CreateSearchBlob(
            [
                item.DisplayName,
                item.KeyName,
                item.Entry.RegistryPath,
                item.Entry.BackendRegistryPath,
                item.Entry.SourceRootPath,
                item.Entry.CommandText,
                item.Entry.HandlerClsid,
                item.Entry.FilePath,
                item.Notes,
                item.UserNote,
                categoryName,
                item.StateLabel
            ])
        };

        return new Candidate(GlobalSearchScope.Classic, result)
        {
            Entry = item.Entry,
            UserNote = item.UserNote
        };
    }

    private IEnumerable<Candidate> CreateWindows11Candidates(IReadOnlyList<Windows11ContextMenuItemDefinition> items)
    {
        return items
            .GroupBy(CreateWindows11GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => CreateWindows11Candidate(group.ToArray()));
    }

    private Candidate CreateWindows11Candidate(IReadOnlyList<Windows11ContextMenuItemDefinition> definitions)
    {
        var primary = definitions[0];
        var displayName = primary.SourceKind == Windows11ContextMenuSourceKind.SystemCommandStore
            ? primary.DisplayName : primary.Package.DisplayName;
        var isEnabled = definitions.All(static definition => definition.IsEnabled);
        var stateLabel = _localization.Translate(!isEnabled && definitions.Any(definition => definition.IsEnabled)
            ? "Windows11PartiallyEnabled" : isEnabled ? "Enabled" : "Disabled");
        var contextTypesText = string.Join(
            "  ·  ",
            definitions
                .SelectMany(static definition => definition.ContextTypes)
                .Where(static contextType => !string.IsNullOrWhiteSpace(contextType))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(LocalizeContextType));
        var scopeLabel = _localization.Translate("GlobalSearchWin11Scope");
        var filePath = primary.ComServer.Path ?? primary.Entry?.FilePath ?? primary.Package.InstallPath;
        var iconSource = TryGetWindows11Icon(primary);
        var userNote = _settingsService.GetContextMenuItemNote(primary.Id);
        var result = new GlobalSearchResultViewModel
        {
            Id = primary.Id,
            DisplayName = displayName,
            StateLabel = stateLabel,
            IsEnabled = isEnabled,
            ScopeLabel = scopeLabel,
            Scope = GlobalSearchScope.Windows11,
            Category = null,
            IsWindows11 = true,
            TargetPageType = typeof(Windows11ContextMenuPage),
            TargetFilterText = displayName,
            RegistryPath = primary.Entry?.RegistryPath,
            CommandText = primary.Entry?.CommandText,
            FilePath = filePath,
            HandlerClsid = primary.ComServer.Id ?? primary.Entry?.HandlerClsid,
            IconSource = iconSource,
            FallbackIconSymbol = SymbolRegular.AppsListDetail20,
            JumpText = _localization.Translate("GlobalSearchJump"),
            SearchBlob = ContextMenuSearchMatcher.CreateSearchBlob(
            [
                primary.DisplayName,
                primary.Package.FullName,
                primary.Package.DisplayName,
                primary.Package.FamilyName,
                primary.Package.PublisherDisplayName,
                contextTypesText,
                primary.ComServer.Path,
                primary.ComServer.Id,
                primary.Entry?.RegistryPath,
                primary.Entry?.BackendRegistryPath,
                primary.Entry?.SourceRootPath,
                primary.Entry?.FilePath,
                primary.Entry?.HandlerClsid,
                userNote,
                stateLabel,
                scopeLabel
            ])
        };

        return new Candidate(GlobalSearchScope.Windows11, result)
        {
            Windows11Definitions = definitions,
            PackageFamilyName = primary.Package.FamilyName,
            PublisherName = primary.Package.PublisherDisplayName,
            ContextTypesText = contextTypesText,
            UserNote = userNote
        };
    }

    private string LocalizeContextType(string? contextType)
    {
        if (string.IsNullOrWhiteSpace(contextType))
        {
            return string.Empty;
        }

        return contextType switch
        {
            "Directory" => _localization.Translate("FolderCategoryName"),
            @"Directory\Background" => _localization.Translate("BackgroundCategoryName"),
            "Drive" => _localization.Translate("DriveCategoryName"),
            "*" => _localization.Translate("FileCategoryName"),
            "DesktopBackground" => _localization.Translate("DesktopCategoryName"),
            _ => contextType
        };
    }

    private static string CreateWindows11GroupKey(Windows11ContextMenuItemDefinition item) =>
        item.SourceKind == Windows11ContextMenuSourceKind.SystemCommandStore
            ? item.Id : "win11-app|" + item.Package.FamilyName;

    private static Type GetTargetPageType(ContextMenuCategory category) => category switch
    {
        ContextMenuCategory.File => typeof(FileContextMenuPage),
        ContextMenuCategory.AllFileSystemObjects => typeof(AllObjectsContextMenuPage),
        ContextMenuCategory.Folder => typeof(FolderContextMenuPage),
        ContextMenuCategory.Directory => typeof(DirectoryContextMenuPage),
        ContextMenuCategory.DirectoryBackground => typeof(BackgroundContextMenuPage),
        ContextMenuCategory.DesktopBackground => typeof(DesktopContextMenuPage),
        ContextMenuCategory.Drive => typeof(DriveContextMenuPage),
        ContextMenuCategory.Library => typeof(LibraryContextMenuPage),
        ContextMenuCategory.Computer => typeof(ComputerContextMenuPage),
        ContextMenuCategory.RecycleBin => typeof(RecycleBinContextMenuPage),
        _ => typeof(FileContextMenuPage)
    };

    private ImageSource? TryGetWindows11Icon(Windows11ContextMenuItemDefinition definition)
    {
        if (definition.Entry is { } entry)
        {
            return TryGetEntryIcon(entry, definition.ComServer.Path ?? definition.Package.InstallPath);
        }

        if (!string.IsNullOrWhiteSpace(definition.ComServer.Path))
        {
            return _iconPreviewService.GetIcon(null, 0, definition.ComServer.Path);
        }

        if (!string.IsNullOrWhiteSpace(definition.Package.InstallPath))
        {
            return _iconPreviewService.GetIcon(null, 0, definition.Package.InstallPath);
        }

        return null;
    }

    private ImageSource? TryGetClassicIcon(ContextMenuItemViewModel item)
    {
        return HasIconSourceData(item.Entry.IconPath, item.Entry.FilePath)
            ? item.IconSource
            : null;
    }

    private ImageSource? TryGetEntryIcon(ContextMenuEntry entry, string? fallbackFilePath)
    {
        var resolvedFallbackFilePath = entry.FilePath ?? fallbackFilePath;
        return HasIconSourceData(entry.IconPath, resolvedFallbackFilePath)
            ? _iconPreviewService.GetIcon(entry.IconPath, entry.IconIndex, resolvedFallbackFilePath)
            : null;
    }

    private static bool HasIconSourceData(string? iconPath, string? fallbackFilePath)
    {
        return !string.IsNullOrWhiteSpace(iconPath)
               || !string.IsNullOrWhiteSpace(fallbackFilePath);
    }

    private static SymbolRegular GetFallbackIconSymbol(ContextMenuCategory category) => category switch
    {
        ContextMenuCategory.Folder
            or ContextMenuCategory.Directory
            or ContextMenuCategory.DirectoryBackground
            or ContextMenuCategory.DesktopBackground
            or ContextMenuCategory.Library => SymbolRegular.Folder20,
        ContextMenuCategory.File
            or ContextMenuCategory.AllFileSystemObjects => SymbolRegular.Document20,
        _ => SymbolRegular.AppGeneric20
    };

    private sealed record Candidate(GlobalSearchScope Scope, GlobalSearchResultViewModel Result)
    {
        public ContextMenuEntry? Entry { get; init; }

        public IReadOnlyList<Windows11ContextMenuItemDefinition>? Windows11Definitions { get; init; }

        public string? PackageFamilyName { get; init; }

        public string? PublisherName { get; init; }

        public string? ContextTypesText { get; init; }

        public string? UserNote { get; init; }
    }

    private sealed record ScoredCandidate(Candidate Candidate, int Score);
}
