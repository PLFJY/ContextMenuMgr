using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;

namespace ContextMenuMgr.Frontend.ViewModels;

public sealed class DeepAnalysisMenuItemViewModel
{
    private DeepAnalysisMenuItemViewModel(ContextMenuDeepAnalysisMenuItem item, LocalizationService localization)
    {
        RawText = item.RawText;
        Text = ResolveDisplayText(item, localization);
        CanonicalVerb = item.CanonicalVerb;
        HelpText = item.HelpText;
        CommandOffset = item.CommandOffset;
        IsSeparator = item.IsSeparator;
        IsSubmenu = item.IsSubmenu;
        IconImage = DecodeIcon(item.IconPngBase64);

        foreach (var child in item.Children)
        {
            Children.Add(new DeepAnalysisMenuItemViewModel(child, localization));
        }
    }

    public string? RawText { get; }

    public string Text { get; }

    public string? CanonicalVerb { get; }

    public string? HelpText { get; }

    public int CommandOffset { get; }

    public bool IsSeparator { get; }

    public bool IsSubmenu { get; }

    public ImageSource? IconImage { get; }

    public ObservableCollection<DeepAnalysisMenuItemViewModel> Children { get; } = [];

    public static DeepAnalysisMenuItemViewModel FromContract(ContextMenuDeepAnalysisMenuItem item, LocalizationService localization)
    {
        return new DeepAnalysisMenuItemViewModel(item, localization);
    }

    private static string ResolveDisplayText(ContextMenuDeepAnalysisMenuItem item, LocalizationService localization)
    {
        if (!string.IsNullOrWhiteSpace(item.Text))
        {
            return item.Text;
        }

        return localization.Translate(item.IsSeparator ? "DeepAnalyzeSeparatorText" : "DeepAnalyzeNoTextItem");
    }

    private static ImageSource? DecodeIcon(string? iconPngBase64)
    {
        if (string.IsNullOrWhiteSpace(iconPngBase64))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(iconPngBase64);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}

public sealed partial class ContextMenuDeepAnalysisWindowViewModel : ObservableObject
{
    private readonly LocalizationService _localization;
    private readonly Func<ContextMenuDeepAnalysisRequest, Task<ContextMenuDeepAnalysisResult>> _runProbeAsync;
    private readonly ContextMenuDeepAnalysisRequest _baseRequest;
    private Action? _closeAction;
    private Guid _currentOperationId;

    public ContextMenuDeepAnalysisWindowViewModel(
        LocalizationService localization,
        ContextMenuDeepAnalysisRequest request,
        Func<ContextMenuDeepAnalysisRequest, Task<ContextMenuDeepAnalysisResult>> runProbeAsync)
    {
        _localization = localization;
        _baseRequest = request;
        _runProbeAsync = runProbeAsync;
        Title = _localization.Translate("DeepAnalysisWindowTitle");
        DisplayName = request.DisplayName;
        EntryKindText = GetEntryKindText(request.EntryKind);
        HandlerClsid = request.HandlerClsid ?? string.Empty;
        RegistryPath = request.RegistryPath ?? string.Empty;
        HandlerFilePath = request.HandlerFilePath ?? string.Empty;
        ProbeModeText = GetProbeModeText(request.ProbeMode);
    }

    [ObservableProperty]
    public partial string Title { get; private set; }

    [ObservableProperty]
    public partial string DisplayName { get; private set; }

    [ObservableProperty]
    public partial string EntryKindText { get; private set; }

    [ObservableProperty]
    public partial string HandlerClsid { get; private set; }

    [ObservableProperty]
    public partial string RegistryPath { get; private set; }

    [ObservableProperty]
    public partial string HandlerFilePath { get; private set; }

    [ObservableProperty]
    public partial string ProbeModeText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResult))]
    [NotifyPropertyChangedFor(nameof(ShowErrorCode))]
    public partial bool IsRunning { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsSuccess { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowErrorCode))]
    public partial bool IsFailure { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowExpectedFailureBadge))]
    public partial bool IsExpectedFailure { get; private set; }

    [ObservableProperty]
    public partial string FailureTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string FailureMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowErrorCode))]
    public partial string ErrorCode { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDiagnosticsExpander))]
    public partial string DiagnosticsText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanRunWholeContextMenu { get; private set; }

    public ObservableCollection<DeepAnalysisMenuItemViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public bool ShowResult => !IsRunning;

    public bool ShowNoItems => IsSuccess && !HasItems;

    public bool ShowWholeContextMenuWarning { get; private set; }

    public bool ShowErrorCode => IsFailure && !string.IsNullOrWhiteSpace(ErrorCode);

    public bool ShowExpectedFailureBadge => IsExpectedFailure;

    public bool ShowDiagnosticsExpander => ShowDeepAnalysisDiagnostics && !string.IsNullOrWhiteSpace(DiagnosticsText);

#if DEBUG
    public bool ShowDeepAnalysisDiagnostics { get; } = true;
#else
    public bool ShowDeepAnalysisDiagnostics { get; } = false;
#endif

    public string RunningText => _localization.Translate("DeepAnalysisRunning");

    public string SuccessText => _localization.Translate("DeepAnalysisSuccess");

    public string FailedText => _localization.Translate("DeepAnalysisFailed");

    public string NoItemsText => _localization.Translate("DeepAnalysisNoItems");

    public string DiagnosticsHeader => _localization.Translate("DeepAnalysisDiagnosticsHeader");

    public string CopyDiagnosticsText => _localization.Translate("DeepAnalysisCopyDiagnostics");

    public string WholeContextMenuText => _localization.Translate("DeepAnalyzeWholeContextMenuButton");

    public string WholeContextMenuWarning => _localization.Translate("DeepAnalyzeWholeContextMenuWarning");

    public string WholeContextMenuFallbackHint => _localization.Translate("DeepAnalysisWholeMenuFallbackHint");

    public string ExpectedFailureBadgeText => _localization.Translate("DeepAnalysisExpectedFailureBadge");

    public string MetadataTitle => _localization.Translate("DeepAnalysisMetadataTitle");

    public string DisclaimerText => _localization.Translate("DeepAnalyzeDisclaimer");

    public string HandlerFilePathLabel => _localization.Translate("DeepAnalyzeHandlerFilePathLabel");

    public string RegistryPathLabel => _localization.Translate("DeepAnalyzeRegistryPathLabel");

    public string HandlerClsidLabel => _localization.Translate("DeepAnalyzeHandlerClsidLabel");

    public string CloseText => _localization.Translate("DialogClose");

    public void SetCloseAction(Action closeAction)
    {
        _closeAction = closeAction;
    }

    public async Task RunAsync()
    {
        await RunRequestAsync(_baseRequest);
    }

    [RelayCommand]
    private async Task RunWholeContextMenuAsync()
    {
        var request = _baseRequest with
        {
            OperationId = Guid.NewGuid(),
            ProbeMode = ContextMenuDeepAnalysisProbeMode.WholeContextMenu
        };
        FrontendDebugLog.Operation(
            "FrontendOperation",
            $"WholeContextMenuManualStart: OperationId={request.OperationId}, ItemId={request.ItemId}, HandlerClsid={request.HandlerClsid}.");
        await RunRequestAsync(request);
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        if (!string.IsNullOrWhiteSpace(DiagnosticsText))
        {
            Clipboard.SetText(DiagnosticsText);
        }
    }

    [RelayCommand]
    private void Close()
    {
        _closeAction?.Invoke();
    }

    private async Task RunRequestAsync(ContextMenuDeepAnalysisRequest request)
    {
        if (request.OperationId == Guid.Empty)
        {
            request = request with { OperationId = Guid.NewGuid() };
        }

        _currentOperationId = request.OperationId;
        IsRunning = true;
        IsSuccess = false;
        IsFailure = false;
        IsExpectedFailure = false;
        CanRunWholeContextMenu = false;
        ShowWholeContextMenuWarning = false;
        FailureTitle = string.Empty;
        FailureMessage = string.Empty;
        ErrorCode = string.Empty;
        DiagnosticsText = string.Empty;
        HandlerFilePath = request.HandlerFilePath ?? string.Empty;
        Items.Clear();
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowNoItems));
        OnPropertyChanged(nameof(ShowWholeContextMenuWarning));
        OnPropertyChanged(nameof(ShowDiagnosticsExpander));
        ProbeModeText = GetProbeModeText(request.ProbeMode);

        var result = await _runProbeAsync(request);
        if (result.OperationId != _currentOperationId)
        {
            FrontendDebugLog.Warning(
                "ContextMenuDeepAnalysisWindowViewModel",
                $"DeepAnalysisStaleResultIgnored: CurrentOperationId={_currentOperationId}, ResultOperationId={result.OperationId}, ItemId={request.ItemId}, ProbeMode={request.ProbeMode}.");
            return;
        }

        ApplyResult(result);
    }

    private void ApplyResult(ContextMenuDeepAnalysisResult result)
    {
        IsRunning = false;
        IsSuccess = result.Success;
        IsFailure = !result.Success;
        ErrorCode = result.ErrorCode ?? string.Empty;
        IsExpectedFailure = IsExpectedLimitation(result.ErrorCode);
        var protocolFailure = IsProtocolFailure(result.ErrorCode);
        FailureTitle = IsExpectedFailure
            ? _localization.Translate("DeepAnalysisUnableToResolveTitle")
            : _localization.Translate("DeepAnalysisFailed");
        FailureMessage = IsExpectedFailure
            ? _localization.Translate("DeepAnalysisUnableToResolveNormalMessage")
            : GetFailureMessage(result, protocolFailure);
        HandlerFilePath = result.HandlerFilePath ?? HandlerFilePath;
        ProbeModeText = GetProbeModeText(result.ProbeMode);
        ShowWholeContextMenuWarning = result.ProbeMode == ContextMenuDeepAnalysisProbeMode.WholeContextMenu;
        CanRunWholeContextMenu = !result.Success
            && result.ProbeMode == ContextMenuDeepAnalysisProbeMode.SpecificHandler
            && IsExpectedFailure
            && ContextMenuDeepAnalysisCapability.CanDeepAnalyzeCategory(_baseRequest.Category);
        DiagnosticsText = BuildDiagnostics(result);

        Items.Clear();
        foreach (var item in result.Items)
        {
            Items.Add(DeepAnalysisMenuItemViewModel.FromContract(item, _localization));
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowNoItems));
        OnPropertyChanged(nameof(ShowWholeContextMenuWarning));
        OnPropertyChanged(nameof(ShowExpectedFailureBadge));
        OnPropertyChanged(nameof(ShowDiagnosticsExpander));
    }

    private string GetFailureMessage(ContextMenuDeepAnalysisResult result, bool protocolFailure)
    {
        if (protocolFailure)
        {
            return _localization.Translate("DeepAnalyzeProtocolFailedText");
        }

        return result.ErrorCode switch
        {
            _ => result.Message ?? string.Empty
        };
    }

    private string GetProbeModeText(ContextMenuDeepAnalysisProbeMode mode)
    {
        return _localization.Translate(mode == ContextMenuDeepAnalysisProbeMode.WholeContextMenu
            ? "DeepAnalysisProbeModeWholeMenu"
            : "DeepAnalysisProbeModeSpecific");
    }

    private string GetEntryKindText(ContextMenuEntryKind entryKind)
    {
        return entryKind == ContextMenuEntryKind.ShellExtension
            ? _localization.Translate("DeepAnalysisEntryKindShellExtension")
            : entryKind.ToString();
    }

    private static bool IsProtocolFailure(string? errorCode)
    {
        return errorCode is "InvalidProbeHostOutput"
            or "InvalidProbeHostJson"
            or "ProbeHostEmptyResult"
            or "ProbeHostNotFound"
            or "ProbeHostStartFailed";
    }

    private static bool IsExpectedLimitation(string? errorCode)
    {
        return errorCode is "CoCreateHandlerFailed"
            or "CoCreateHandlerNoIUnknown"
            or "ShellExtInitNotSupported"
            or "ShellExtInitInitializeFailed"
            or "IContextMenuNotSupported"
            or "QueryContextMenuFailed"
            or "SpecificHandlerReturnedNoItems"
            or "SpecificHandlerBackgroundInitializationFailed"
            or "NoMenuItemsFound"
            or "UnsupportedCategory"
            or "UnsupportedCategoryForRuntimeProbe";
    }

    private static string BuildDiagnostics(ContextMenuDeepAnalysisResult result)
    {
        return string.Join(
            Environment.NewLine,
            new[]
            {
                $"ErrorCode={result.ErrorCode}",
                $"OperationId={result.OperationId}",
                $"Message={result.Message}",
                $"ProbeMode={result.ProbeMode}",
                $"HandlerFilePath={result.HandlerFilePath}",
                $"HandlerFileExists={result.HandlerFileExists}",
                $"HandlerMachineType={result.HandlerMachineType ?? result.HandlerFileMachineType}",
                $"HandlerMachineRawValue={result.HandlerMachineRawValue}",
                $"SelectedProbeHostArchitecture={result.SelectedProbeHostArchitecture}",
                $"SelectedProbeHostPath={result.SelectedProbeHostPath}",
                $"ActualProbeHostMachineType={result.ActualProbeHostMachineType}",
                $"ArchitectureSelectionReason={result.ArchitectureSelectionReason}",
                $"OSArchitecture={result.OSArchitecture}",
                $"FrontendProcessArchitecture={result.FrontendProcessArchitecture}",
                $"ProbeHostProcessArchitecture={result.ProbeHostProcessArchitecture}",
                result.DiagnosticDetails,
                BuildItemDiagnostics(result.Items)
            }.Where(static line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string BuildItemDiagnostics(IReadOnlyList<ContextMenuDeepAnalysisMenuItem> items)
    {
        if (items.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string> { "Items:" };
        AppendItems(lines, items, 0);
        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendItems(List<string> lines, IReadOnlyList<ContextMenuDeepAnalysisMenuItem> items, int depth)
    {
        var indent = new string(' ', depth * 2);
        foreach (var item in items)
        {
            lines.Add($"{indent}- Text={item.Text}; RawText={item.RawText}; CanonicalVerb={item.CanonicalVerb}; CommandOffset={item.CommandOffset}; IsSeparator={item.IsSeparator}");
            if (item.Children.Count > 0)
            {
                AppendItems(lines, item.Children, depth + 1);
            }
        }
    }
}
