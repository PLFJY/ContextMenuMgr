using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
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
    private readonly Dictionary<string, Candidate> _classic = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirtyIds = new(StringComparer.OrdinalIgnoreCase);
    private List<Candidate> _windows11 = [];
    private bool _rebuildAll = true;
    private bool _windows11Dirty = true;
    private DispatcherOperation? _pendingUpdate;
    private readonly CancellationTokenSource _lifetime = new();

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
        if (_pendingUpdate is not null)
        {
            _pendingUpdate.Abort();
            _pendingUpdate = null;
            RebuildCandidates(notify: false);
        }
        List<Candidate> snapshot;
        lock (_gate) snapshot = _candidates;
        var matches = string.IsNullOrWhiteSpace(query)
            ? snapshot.OrderBy(candidate => candidate.Result.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(candidate => candidate.Result.Id, StringComparer.OrdinalIgnoreCase).Take(limit).ToArray()
            : snapshot.Select(candidate => new ScoredCandidate(candidate, Score(candidate, query)))
                .Where(candidate => candidate.Score > 0).OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Candidate.Result.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Take(limit).Select(candidate => candidate.Candidate).ToArray();
        foreach (var candidate in matches) _ = LoadResultIconAsync(candidate);
        return matches.Select(candidate => candidate.Result).ToArray();
    }

    private async Task LoadResultIconAsync(Candidate candidate)
    {
        if (candidate.IconRequested || _disposed) return;
        candidate.IconRequested = true;
        try
        {
            var entry = candidate.Entry ?? candidate.Windows11Definitions?.FirstOrDefault()?.Entry;
            var icon = await _iconPreviewService.GetIconAsync(entry?.IconPath, entry?.IconIndex ?? 0,
                entry?.FilePath ?? candidate.Result.FilePath, _lifetime.Token);
            if (!_disposed) candidate.Result.IconSource = icon;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FrontendDebugLog.Warning(nameof(ContextMenuGlobalSearchService), $"Search icon skipped: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pendingUpdate?.Abort();
        _lifetime.Cancel();
        _lifetime.Dispose();
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

    private void OnLanguageChanged(object? sender, EventArgs e) => QueueUpdate(all: true);
    private void OnSettingsChanged(object? sender, EventArgs e) => QueueUpdate(all: true);
    private void OnWindows11ItemsChanged(object? sender, EventArgs e) => QueueUpdate(windows11: true);

    private void OnWorkspaceItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (ContextMenuItemViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnWorkspaceItemPropertyChanged;
                _dirtyIds.Add(item.Id);
            }
        if (e.OldItems is not null)
            foreach (ContextMenuItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnWorkspaceItemPropertyChanged;
                _dirtyIds.Add(item.Id);
            }
        QueueUpdate(all: e.Action == NotifyCollectionChangedAction.Reset);
    }

    private void OnWorkspaceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ContextMenuItemViewModel item) return;
        if (e.PropertyName == nameof(ContextMenuItemViewModel.IconSource))
        {
            if (_classic.TryGetValue(item.Id, out var candidate)) candidate.Result.IconSource = item.IconSource;
            return;
        }
        if (e.PropertyName is nameof(ContextMenuItemViewModel.DisplayName) or nameof(ContextMenuItemViewModel.IsEnabled)
            or nameof(ContextMenuItemViewModel.IsDeleted) or nameof(ContextMenuItemViewModel.IsWindows11ContextMenu)
            or nameof(ContextMenuItemViewModel.UserNote))
        {
            _dirtyIds.Add(item.Id);
            QueueUpdate();
        }
    }

    private void QueueUpdate(bool all = false, bool windows11 = false)
    {
        if (_disposed) return;
        _rebuildAll |= all;
        _windows11Dirty |= windows11 || all;
        if (_pendingUpdate is not null) return;
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _pendingUpdate = dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
        {
            _pendingUpdate = null;
            if (!_disposed) RebuildCandidates();
        }));
    }

    private void RebuildCandidates(bool notify = true)
    {
        if (_rebuildAll) { _classic.Clear(); _dirtyIds.Clear(); }
        foreach (var item in _workspace.Items)
            if (_rebuildAll || _dirtyIds.Remove(item.Id))
            {
                if (IsClassicGlobalSearchItem(item)) _classic[item.Id] = CreateClassicCandidate(item);
                else _classic.Remove(item.Id);
            }
        foreach (var removed in _dirtyIds) _classic.Remove(removed);
        _dirtyIds.Clear();
        if (_windows11Dirty)
            _windows11 = _windows11Service.IsSupported ? CreateWindows11Candidates(_windows11Service.CurrentItems).ToList() : [];
        _rebuildAll = _windows11Dirty = false;
        lock (_gate) _candidates = _classic.Values.Concat(_windows11).ToList();
        if (notify) CandidatesChanged?.Invoke(this, EventArgs.Empty);
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
            "  路  ",
            definitions
                .SelectMany(static definition => definition.ContextTypes)
                .Where(static contextType => !string.IsNullOrWhiteSpace(contextType))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(LocalizeContextType));
        var scopeLabel = _localization.Translate("GlobalSearchWin11Scope");
        var filePath = primary.ComServer.Path ?? primary.Entry?.FilePath ?? primary.Package.InstallPath;
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
        public bool IconRequested { get; set; }
        public ContextMenuEntry? Entry { get; init; }

        public IReadOnlyList<Windows11ContextMenuItemDefinition>? Windows11Definitions { get; init; }

        public string? PackageFamilyName { get; init; }

        public string? PublisherName { get; init; }

        public string? ContextTypesText { get; init; }

        public string? UserNote { get; init; }
    }

    private sealed record ScoredCandidate(Candidate Candidate, int Score);
}
