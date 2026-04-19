using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

/// <summary>
/// Represents the windows11 Context Menu Item View Model.
/// </summary>
public partial class Windows11ContextMenuItemViewModel : ObservableObject, IDisposable
{
    private readonly Windows11ContextMenuService _service;
    private readonly LocalizationService _localization;
    private readonly EventHandler _languageChangedHandler;
    private bool _suppressSync;
    private readonly Windows11ContextMenuItemDefinition _primaryDefinition;

    /// <summary>
    /// Initializes a new instance of the <see cref="Windows11ContextMenuItemViewModel"/> class.
    /// </summary>
    public Windows11ContextMenuItemViewModel(
        IReadOnlyList<Windows11ContextMenuItemDefinition> definitions,
        Windows11ContextMenuService service,
        LocalizationService localization)
    {
        Definitions = definitions;
        _primaryDefinition = definitions[0];
        _service = service;
        _localization = localization;

        _logoSource = new Lazy<ImageSource?>(() => _service.LoadLogo(_primaryDefinition.Package.LogoPath));
        IsEnabled = definitions.All(static definition => definition.IsEnabled);

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

    /// <summary>
    /// Gets the grouped source definitions.
    /// </summary>
    public IReadOnlyList<Windows11ContextMenuItemDefinition> Definitions { get; }

    public string DisplayName => _primaryDefinition.DisplayName;

    public string PublisherName => _primaryDefinition.Package.PublisherDisplayName ?? string.Empty;

    public string PackageFamilyName => _primaryDefinition.Package.FamilyName;

    public string InstallPath => _primaryDefinition.Package.InstallPath;

    public string ComServerPath => _primaryDefinition.ComServer.Path ?? string.Empty;

    public string ContextTypesText => string.Join(
        "  ·  ",
        Definitions
            .SelectMany(static definition => definition.ContextTypes)
            .Where(static type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(LocalizeContextType));

    public bool HasComServerPath => !string.IsNullOrWhiteSpace(_primaryDefinition.ComServer.Path);

    public bool HasPublisherName => !string.IsNullOrWhiteSpace(_primaryDefinition.Package.PublisherDisplayName);

    public ImageSource? LogoSource => _logoSource.Value;

    public bool HasLogo => LogoSource is not null;

    public string ToggleOnText => _localization.Translate("ToggleOn");

    public string ToggleOffText => _localization.Translate("ToggleOff");

    public string MachineBlockedText => _localization.Translate("Windows11MachineBlockedText");

    public string OpenFileLocationText => _localization.Translate("DetailsFileLocation");

    /// <summary>
    /// Gets or sets a value indicating whether enabled.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether busy.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsBusy { get; set; }

    public bool IsMachineBlocked => Definitions.Any(static definition => definition.IsMachineBlocked);

    public bool CanToggle => !IsBusy && !IsMachineBlocked;

    /// <summary>
    /// Refreshes state.
    /// </summary>
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
            var actualStates = new List<bool>(Definitions.Count);
            foreach (var definition in Definitions)
            {
                actualStates.Add(await _service.SetEnabledAsync(definition.Id, newValue, CancellationToken.None));
            }

            RefreshState(actualStates.All(static state => state));
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

    /// <summary>
    /// Executes dispose.
    /// </summary>
    public void Dispose()
    {
        _localization.LanguageChanged -= _languageChangedHandler;
    }
}
