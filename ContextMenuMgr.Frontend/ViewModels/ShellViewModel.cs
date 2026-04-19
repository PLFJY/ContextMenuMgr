using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Wpf.Ui.Controls;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the shell View Model.
/// </summary>
public partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly ContextMenuWorkspaceService _workspace;
    private readonly LocalizationService _localization;
    private readonly Windows11ContextMenuService _windows11Service;
    private readonly InfoBadge _approvalsBadge = new() { Visibility = Visibility.Collapsed };

    /// <summary>
    /// Initializes a new instance of the <see cref="ShellViewModel"/> class.
    /// </summary>
    public ShellViewModel(
        ContextMenuWorkspaceService workspace,
        LocalizationService localization,
        Windows11ContextMenuService windows11Service)
    {
        _workspace = workspace;
        _localization = localization;
        _windows11Service = windows11Service;

        _localization.LanguageChanged += OnLanguageChanged;
        _workspace.PendingApprovalDetected += OnPendingApprovalDetected;
        _workspace.PropertyChanged += OnWorkspacePropertyChanged;
        _workspace.Items.CollectionChanged += OnWorkspaceItemsCollectionChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged += OnWorkspaceItemPropertyChanged;
        }

        UpdateApprovalBadge();
    }

    public ObservableCollection<ToastNotificationViewModel> Notifications => _workspace.Notifications;

    public string WindowTitle => _localization.Translate("WindowTitle");

    public string AppTitle => _localization.Translate("AppTitle");

    public string RefreshText => _localization.Translate("Refresh");

    public string ConnectionStatus => _workspace.ConnectionStatus;

    public string ServiceAttentionText => _workspace.ServiceAttentionText;

    public bool HasServiceAttention => !string.IsNullOrWhiteSpace(ServiceAttentionText);

    public string FileCategoryName => _localization.Translate("FileCategoryName");

    public string AllObjectsCategoryName => _localization.Translate("AllObjectsCategoryName");

    public string FolderCategoryName => _localization.Translate("FolderCategoryName");

    public string DirectoryCategoryName => _localization.Translate("DirectoryCategoryName");

    public string BackgroundCategoryName => _localization.Translate("BackgroundCategoryName");

    public string DesktopCategoryName => _localization.Translate("DesktopCategoryName");

    public string DriveCategoryName => _localization.Translate("DriveCategoryName");

    public string LibraryCategoryName => _localization.Translate("LibraryCategoryName");

    public string ComputerCategoryName => _localization.Translate("ComputerCategoryName");

    public string RecycleBinCategoryName => _localization.Translate("RecycleBinCategoryName");

    public string FileTypesPageTitle => _localization.Translate("FileTypesPageTitle");

    public string Windows11PageTitle => _localization.Translate("Windows11PageTitle");

    public bool IsWindows11ContextMenuSupported => _windows11Service.IsSupported;

    public string OtherRulesPageTitle => _localization.Translate("OtherRulesPageTitle");

    public string ApprovalsTitle => _localization.Translate("PendingApprovalTitle");

    public string SettingsTitle => _localization.Translate("SettingsTitle");

    public InfoBadge ApprovalsBadge => _approvalsBadge;

    public event EventHandler<ContextMenuEntry>? PendingApprovalDetected;

    /// <summary>
    /// Initializes async.
    /// </summary>
    public async Task InitializeAsync(bool suppressBootstrapPrompt = false)
    {
        await _workspace.InitializeAsync(suppressBootstrapPrompt);
        UpdateApprovalBadge();
    }

    /// <summary>
    /// Stops monitoring Async.
    /// </summary>
    public Task<BackendServiceBootstrapResult> StopMonitoringAsync()
    {
        return _workspace.StopMonitoringAsync();
    }

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        _workspace.PendingApprovalDetected -= OnPendingApprovalDetected;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        _workspace.Items.CollectionChanged -= OnWorkspaceItemsCollectionChanged;
        foreach (var item in _workspace.Items)
        {
            item.PropertyChanged -= OnWorkspaceItemPropertyChanged;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _workspace.RefreshAsync();
        if (_windows11Service.IsSupported)
        {
            await _windows11Service.RefreshAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    private Task AllowNotificationAsync(ToastNotificationViewModel? notification)
    {
        return ResolveNotificationAsync(notification, ContextMenuDecision.Allow);
    }

    [RelayCommand]
    private Task DenyNotificationAsync(ToastNotificationViewModel? notification)
    {
        return ResolveNotificationAsync(notification, ContextMenuDecision.Deny);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(AppTitle));
        OnPropertyChanged(nameof(RefreshText));
        _workspace.RefreshServiceAttentionText();
        OnPropertyChanged(nameof(ServiceAttentionText));
        OnPropertyChanged(nameof(HasServiceAttention));
        OnPropertyChanged(nameof(FileCategoryName));
        OnPropertyChanged(nameof(AllObjectsCategoryName));
        OnPropertyChanged(nameof(FolderCategoryName));
        OnPropertyChanged(nameof(DirectoryCategoryName));
        OnPropertyChanged(nameof(BackgroundCategoryName));
        OnPropertyChanged(nameof(DesktopCategoryName));
        OnPropertyChanged(nameof(DriveCategoryName));
        OnPropertyChanged(nameof(LibraryCategoryName));
        OnPropertyChanged(nameof(ComputerCategoryName));
        OnPropertyChanged(nameof(RecycleBinCategoryName));
        OnPropertyChanged(nameof(FileTypesPageTitle));
        OnPropertyChanged(nameof(Windows11PageTitle));
        OnPropertyChanged(nameof(OtherRulesPageTitle));
        OnPropertyChanged(nameof(ApprovalsTitle));
        OnPropertyChanged(nameof(SettingsTitle));
        UpdateApprovalBadge();
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContextMenuWorkspaceService.ConnectionStatus))
        {
            OnPropertyChanged(nameof(ConnectionStatus));
        }

        if (e.PropertyName == nameof(ContextMenuWorkspaceService.ServiceAttentionText))
        {
            OnPropertyChanged(nameof(ServiceAttentionText));
            OnPropertyChanged(nameof(HasServiceAttention));
        }
    }

    private void UpdateApprovalBadge()
    {
        var count = _workspace.Items.Count(static item => item.IsPendingApproval);
        _approvalsBadge.Value = count > 0 ? count.ToString() : string.Empty;
        _approvalsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

        UpdateApprovalBadge();
    }

    private void OnWorkspaceItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContextMenuItemViewModel.IsPendingApproval))
        {
            UpdateApprovalBadge();
        }
    }

    private void OnPendingApprovalDetected(object? sender, ContextMenuEntry e)
    {
        PendingApprovalDetected?.Invoke(this, e);
    }

    private Task ResolveNotificationAsync(ToastNotificationViewModel? notification, ContextMenuDecision decision)
    {
        if (notification is null || string.IsNullOrWhiteSpace(notification.ItemId))
        {
            return Task.CompletedTask;
        }

        return _workspace.ApplyDecisionAsync(notification.ItemId, decision);
    }
}
