using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.ViewModels;
using ContextMenuMgr.Frontend.Views;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Opens the curated per-application menu settings that are backed by the
/// DetailedEdit dictionary. This is intentionally an allow-list for dynamic
/// Shell Extensions, not a claim that arbitrary COM children are editable.
/// </summary>
public sealed class DetailedEditMenuDialogService
{
    private readonly RuleDictionaryCatalogService _catalog;
    private readonly DetailedEditRuleService _rules;
    private readonly LocalizationService _localization;

    public DetailedEditMenuDialogService(RuleDictionaryCatalogService catalog, DetailedEditRuleService rules, LocalizationService localization)
    { _catalog = catalog; _rules = rules; _localization = localization; }

    public bool CanManage(ContextMenuEntry entry) => FindDefinition(entry) is not null;

    public Task ShowAsync(ContextMenuEntry entry)
    {
        var definition = FindDefinition(entry) ?? throw new InvalidOperationException("No curated menu settings are available for this Shell Extension.");
        var viewModel = new DetailedEditGroupViewModel(definition, _rules, _localization);
        new DetailedEditMenuWindow(viewModel, _localization) { Owner = System.Windows.Application.Current?.MainWindow }.Show();
        return Task.CompletedTask;
    }

    private DetailedEditGroupDefinition? FindDefinition(ContextMenuEntry entry)
    {
        if (entry.EntryKind != ContextMenuEntryKind.ShellExtension
            || !Guid.TryParse(entry.HandlerClsid, out var handlerGuid)) return null;
        return _catalog.LoadDetailedEditGroups().FirstOrDefault(group => group.IsAvailable
            && MatchesHandlerClsid(handlerGuid, group.HandlerClsid));
    }

    internal static bool MatchesHandlerClsid(Guid handlerGuid, string? dictionaryClsid)
        => Guid.TryParse(dictionaryClsid, out var dictionaryGuid) && dictionaryGuid == handlerGuid;
}
