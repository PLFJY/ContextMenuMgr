using ContextMenuMgr.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace ContextMenuMgr.Frontend.ViewModels;

public enum GlobalSearchScope
{
    Classic,
    Windows11
}

public sealed partial class GlobalSearchResultViewModel : ObservableObject
{
    private readonly string? _targetFilterText;

    public string Id { get; init; } = string.Empty;

    public bool IsPlaceholder { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string StateLabel { get; init; } = string.Empty;

    public bool IsEnabled { get; init; }

    public bool IsDisabled => !IsEnabled;

    public string ScopeLabel { get; init; } = string.Empty;

    public GlobalSearchScope Scope { get; init; }

    public ContextMenuCategory? Category { get; init; }

    public bool IsWindows11 { get; init; }

    public Type TargetPageType { get; init; } = typeof(object);

    public string? RegistryPath { get; init; }

    public string? CommandText { get; init; }

    public string? FilePath { get; init; }

    public string? HandlerClsid { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIconSource))]
    [NotifyPropertyChangedFor(nameof(HasFallbackIcon))]
    public partial ImageSource? IconSource { get; set; }

    public bool HasIconSource => IconSource is not null;

    public bool HasFallbackIcon => IconSource is null;

    public SymbolRegular FallbackIconSymbol { get; init; } = SymbolRegular.AppGeneric20;

    public string SearchBlob { get; init; } = string.Empty;

    public int Score { get; init; }

    public string JumpText { get; init; } = string.Empty;

    public string TargetFilterText
    {
        get => string.IsNullOrWhiteSpace(_targetFilterText) ? DisplayName : _targetFilterText;
        init => _targetFilterText = value;
    }

    public string? SecondaryText => FirstNonEmpty(RegistryPath, FilePath, CommandText, HandlerClsid);

    public bool HasSecondaryText => !string.IsNullOrWhiteSpace(SecondaryText);

    public override string ToString() => DisplayName;

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }
}
