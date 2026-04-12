using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public partial class Windows11ContextMenuItemViewModel : ObservableObject, IDisposable
{
    private readonly Windows11ContextMenuService _service;
    private readonly LocalizationService _localization;
    private readonly EventHandler _languageChangedHandler;
    private bool _suppressSync;

    public Windows11ContextMenuItemViewModel(
        Windows11ContextMenuItemDefinition definition,
        Windows11ContextMenuService service,
        LocalizationService localization)
    {
        Definition = definition;
        _service = service;
        _localization = localization;

        _logoSource = new Lazy<ImageSource?>(() => _service.LoadLogo(definition.Package.LogoPath));
        IsEnabled = definition.IsEnabled;

        _languageChangedHandler = (_, _) =>
        {
            OnPropertyChanged(nameof(ToggleOnText));
            OnPropertyChanged(nameof(ToggleOffText));
            OnPropertyChanged(nameof(ContextTypesText));
            OnPropertyChanged(nameof(MachineBlockedText));
        };
        _localization.LanguageChanged += _languageChangedHandler;
    }

    private readonly Lazy<ImageSource?> _logoSource;

    public Windows11ContextMenuItemDefinition Definition { get; }

    public string DisplayName => Definition.DisplayName;

    public string PublisherName => Definition.Package.PublisherDisplayName ?? string.Empty;

    public string PackageFamilyName => Definition.Package.FamilyName;

    public string InstallPath => Definition.Package.InstallPath;

    public string ComServerPath => Definition.ComServer.Path ?? string.Empty;

    public string ContextTypesText => string.Join("  ·  ", Definition.ContextTypes.Select(LocalizeContextType));

    public bool HasComServerPath => !string.IsNullOrWhiteSpace(Definition.ComServer.Path);

    public bool HasPublisherName => !string.IsNullOrWhiteSpace(Definition.Package.PublisherDisplayName);

    public ImageSource? LogoSource => _logoSource.Value;

    public bool HasLogo => LogoSource is not null;

    public string ToggleOnText => _localization.Translate("ToggleOn");

    public string ToggleOffText => _localization.Translate("ToggleOff");

    public string MachineBlockedText => _localization.Translate("Windows11MachineBlockedText");

    public string OpenFileLocationText => _localization.Translate("DetailsFileLocation");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsBusy { get; set; }

    public bool IsMachineBlocked => Definition.IsMachineBlocked;

    public bool CanToggle => !IsBusy && !IsMachineBlocked;

    public void RefreshState(bool isEnabled)
    {
        _suppressSync = true;
        try
        {
            IsEnabled = isEnabled;
        }
        finally
        {
            _suppressSync = false;
        }
    }

    partial void OnIsEnabledChanged(bool oldValue, bool newValue)
    {
        if (_suppressSync || oldValue == newValue)
        {
            return;
        }

        IsBusy = true;
        _ = SyncAsync(oldValue, newValue);
    }

    private async Task SyncAsync(bool oldValue, bool newValue)
    {
        try
        {
            var actual = await _service.SetEnabledAsync(Definition.Id, newValue, CancellationToken.None);
            RefreshState(actual);
        }
        catch (Exception ex)
        {
            _suppressSync = true;
            try
            {
                IsEnabled = oldValue;
            }
            finally
            {
                _suppressSync = false;
            }

            await FrontendMessageBox.ShowErrorAsync(ex.Message, DisplayName);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenFileLocationAsync()
    {
        if (string.IsNullOrWhiteSpace(InstallPath) || !Directory.Exists(InstallPath))
        {
            await FrontendMessageBox.ShowErrorAsync(
                _localization.Translate("ModulePathUnavailable"),
                DisplayName);
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{InstallPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            await FrontendMessageBox.ShowErrorAsync(ex.Message, DisplayName);
        }
    }

    private string LocalizeContextType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return string.Empty;
        }

        return type switch
        {
            "Directory" => _localization.Translate("FolderCategoryName"),
            "Directory\\Background" => _localization.Translate("BackgroundCategoryName"),
            "Drive" => _localization.Translate("DriveCategoryName"),
            "*" => _localization.Translate("FileCategoryName"),
            "DesktopBackground" => _localization.Translate("DesktopCategoryName"),
            _ => type
        };
    }

    public void Dispose()
    {
        _localization.LanguageChanged -= _languageChangedHandler;
    }
}
