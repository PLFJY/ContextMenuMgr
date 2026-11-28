using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using ContextMenuMgr.Frontend.Views.Pages;
using Microsoft.Win32;
using Wpf.Ui;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the file type menu analysis page view model.
/// </summary>
public partial class FileTypeMenuAnalysisPageViewModel : ObservableObject, IDisposable
{
    private readonly LocalizationService _localization;
    private readonly IBackendClient _backendClient;
    private readonly INavigationService _navigationService;
    private readonly FileTypesPageViewModel _fileTypesPageViewModel;
    private readonly Func<string> _getInitialFolder;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTypeMenuAnalysisPageViewModel"/> class.
    /// </summary>
    public FileTypeMenuAnalysisPageViewModel(
        IBackendClient backendClient,
        LocalizationService localization,
        INavigationService navigationService,
        FileTypesPageViewModel fileTypesPageViewModel,
        Func<string>? getInitialFolder = null)
    {
        _backendClient = backendClient;
        _localization = localization;
        _navigationService = navigationService;
        _fileTypesPageViewModel = fileTypesPageViewModel;
        _getInitialFolder = getInitialFolder ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        _localization.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<FileTypeAnalysisResult> AnalysisResults { get; } = [];

    public string Title => _localization.Translate("MenuAnalysisTitle");

    public string MenuAnalysisTitle => _localization.Translate("MenuAnalysisTitle");

    public string MenuAnalysisDescription => _localization.Translate("MenuAnalysisDescription");

    public string AnalyzeFileText => _localization.Translate("AnalyzeFile");

    public string AnalyzeFolderText => _localization.Translate("AnalyzeFolder");

    public string JumpText => _localization.Translate("JumpToScene");

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    [RelayCommand]
    private async Task AnalyzeFileAsync()
    {
        var dialog = new OpenFileDialog { DereferenceLinks = false };
        if (dialog.ShowDialog() == true)
        {
            await AnalyzePathAsync(dialog.FileName);
        }
    }

    [RelayCommand]
    private async Task AnalyzeFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = AnalyzeFolderText,
            InitialDirectory = _getInitialFolder()
        };
        if (dialog.ShowDialog() == true)
        {
            await AnalyzePathAsync(dialog.FolderName);
        }
    }

    [RelayCommand]
    private async Task ApplyAnalysisResultAsync(FileTypeAnalysisResult? result)
    {
        if (result is null)
        {
            return;
        }

        if (result.SceneKind == ContextMenuSceneKind.CustomRegistryPath)
        {
            await HandleCustomRegistryPathJumpAsync(result);
            return;
        }

        _navigationService.Navigate(typeof(FileTypesPage));
        await _fileTypesPageViewModel.ApplyAnalysisResultAsync(result);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(MenuAnalysisTitle));
        OnPropertyChanged(nameof(MenuAnalysisDescription));
        OnPropertyChanged(nameof(AnalyzeFileText));
        OnPropertyChanged(nameof(AnalyzeFolderText));
        OnPropertyChanged(nameof(JumpText));
    }

    private async Task HandleCustomRegistryPathJumpAsync(FileTypeAnalysisResult result)
    {
        var scopeValue = result.ScopeValue ?? string.Empty;

        if (scopeValue.Contains(@"HKCR\*\shell", StringComparison.OrdinalIgnoreCase))
        {
            _navigationService.Navigate(typeof(FileContextMenuPage));
        }
        else if (scopeValue.Contains(@"HKCR\AllFilesystemObjects\shell", StringComparison.OrdinalIgnoreCase))
        {
            _navigationService.Navigate(typeof(AllObjectsContextMenuPage));
        }
        else if (scopeValue.Contains(@"HKCR\Folder\shell", StringComparison.OrdinalIgnoreCase))
        {
            _navigationService.Navigate(typeof(FolderContextMenuPage));
        }
        else if (scopeValue.Contains(@"HKCR\Drive\shell", StringComparison.OrdinalIgnoreCase))
        {
            _navigationService.Navigate(typeof(DriveContextMenuPage));
        }
        else if (scopeValue.Contains(@"HKCR\Directory\shell", StringComparison.OrdinalIgnoreCase))
        {
            _navigationService.Navigate(typeof(DirectoryContextMenuPage));
        }
        else
        {
            await FrontendMessageBox.ShowInfoAsync(
                _localization.Format("JumpToCustomPathHint", scopeValue),
                MenuAnalysisTitle);
        }
    }

    private async Task AnalyzePathAsync(string path)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var results = await _backendClient.AnalyzeFileTypeContextAsync(path, cts.Token);
            AnalysisResults.Clear();
            foreach (var result in results)
            {
                var localizedResult = LocalizeAnalysisResult(result);
                AnalysisResults.Add(localizedResult);
            }
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(ex.Message, MenuAnalysisTitle);
        }
    }

    private FileTypeAnalysisResult LocalizeAnalysisResult(FileTypeAnalysisResult result)
    {
        string localizedDisplayName;
        string localizedReason;

        if (result.ScopeValue == @"HKCR\*\shell")
        {
            localizedDisplayName = _localization.Translate("AnalysisResultAllFiles");
            localizedReason = _localization.Translate("AnalysisResultAllFilesReason");
        }
        else if (result.ScopeValue == @"HKCR\AllFilesystemObjects\shell")
        {
            localizedDisplayName = _localization.Translate("AnalysisResultAllFilesystemObjects");
            localizedReason = _localization.Translate("AnalysisResultAllFilesystemObjectsReason");
        }
        else if (result.SceneKind == ContextMenuSceneKind.ExeFile)
        {
            localizedDisplayName = _localization.Translate("AnalysisResultExecutable");
            localizedReason = _localization.Translate("AnalysisResultExecutableReason");
        }
        else if (result.SceneKind == ContextMenuSceneKind.CustomExtension)
        {
            var extension = result.ScopeValue ?? string.Empty;
            localizedDisplayName = _localization.Format("AnalysisResultExtension", extension);
            localizedReason = _localization.Translate("AnalysisResultExtensionReason");
        }
        else if (result.SceneKind == ContextMenuSceneKind.LnkFile)
        {
            localizedDisplayName = _localization.Translate("AnalysisResultShortcut");
            localizedReason = _localization.Translate("AnalysisResultShortcutReason");
        }
        else if (result.SceneKind == ContextMenuSceneKind.UnknownType)
        {
            localizedDisplayName = _localization.Translate("AnalysisResultUnknownType");
            localizedReason = _localization.Translate("AnalysisResultUnknownTypeReason");
        }
        else if (result.SceneKind == ContextMenuSceneKind.PerceivedType)
        {
            var perceivedType = result.ScopeValue ?? string.Empty;
            localizedDisplayName = _localization.Format("AnalysisResultPerceivedType", perceivedType);
            localizedReason = _localization.Translate("AnalysisResultPerceivedTypeReason");
        }
        else if (result.ScopeValue == @"HKCR\Folder\shell")
        {
            localizedDisplayName = _localization.Translate("AnalysisResultFolder");
            localizedReason = _localization.Translate("AnalysisResultFolderReason");
        }
        else if (result.ScopeValue == @"HKCR\Drive\shell")
        {
            localizedDisplayName = _localization.Translate("AnalysisResultDrive");
            localizedReason = _localization.Translate("AnalysisResultDriveReason");
        }
        else if (result.ScopeValue == @"HKCR\Directory\shell")
        {
            localizedDisplayName = _localization.Translate("AnalysisResultDirectory");
            localizedReason = _localization.Translate("AnalysisResultDirectoryReason");
        }
        else if (result.SceneKind == ContextMenuSceneKind.DirectoryType)
        {
            localizedDisplayName = _localization.Translate("AnalysisResultDirectoryType");
            localizedReason = _localization.Translate("AnalysisResultDirectoryTypeReason");
        }
        else
        {
            localizedDisplayName = result.DisplayName;
            localizedReason = result.Reason;
        }

        return result with { DisplayName = localizedDisplayName, Reason = localizedReason };
    }
}
