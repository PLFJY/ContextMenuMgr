#if CONTEXTMENUMGR_IN_PROCESS
using System.Collections.ObjectModel;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>Copies the mutable collections formerly isolated by pipe serialization.</summary>
internal static class InProcessBackendSnapshots
{
    public static PipeRequest Request(PipeRequest request) => request with
    {
        Item = request.Item is { } item ? Entry(item) : null,
        SpecialItem = request.SpecialItem is { } special ? SpecialEntry(special) : null,
        DeletedItemsPurge = request.DeletedItemsPurge is { } purge ? purge with { Items = Copy(purge.Items) } : null
    };

    public static PipeResponse Response(PipeResponse response) => response with
    {
        RemovedItemIds = Copy(response.RemovedItemIds),
        Items = Transform(response.Items, Entry),
        Item = response.Item is { } item ? Entry(item) : null,
        DeletedItemsPurge = Purge(response.DeletedItemsPurge),
        ShellSubMenuItems = Copy(response.ShellSubMenuItems),
        SpecialItems = Transform(response.SpecialItems, SpecialEntry),
        SpecialItem = response.SpecialItem is { } special ? SpecialEntry(special) : null,
        FileTypeAnalysisResults = Copy(response.FileTypeAnalysisResults),
        Win11BlockedItems = Copy(response.Win11BlockedItems),
        OfficeSuiteCoexistence = response.OfficeSuiteCoexistence is { } office ? office with
        {
            WpsIconSourceCandidates = Copy(office.WpsIconSourceCandidates),
            MicrosoftOfficeIconSourceCandidates = Copy(office.MicrosoftOfficeIconSourceCandidates),
            ProtectedAssociations = Transform(office.ProtectedAssociations, association => association with
            {
                MicrosoftProgIds = Copy(association.MicrosoftProgIds),
                WpsProgIdPrefixes = Copy(association.WpsProgIdPrefixes)
            })
        } : null,
        OpenWithInspection = response.OpenWithInspection is { } inspection ? inspection with
        {
            Diagnostics = Copy(inspection.Diagnostics),
            Candidates = Transform(inspection.Candidates, candidate => candidate with
            {
                Sources = Transform(candidate.Sources, source => source with
                {
                    RegistrationPaths = Copy(source.RegistrationPaths),
                    ManageableRegistrationIds = Copy(source.ManageableRegistrationIds)
                })
            })
        } : null
    };

    public static BackendNotification Notification(BackendNotification notification) => notification with
    {
        Item = notification.Item is { } item ? Entry(item) : null,
        SpecialItem = notification.SpecialItem is { } special ? SpecialEntry(special) : null,
        DeletedItemsPurge = Purge(notification.DeletedItemsPurge)
    };

    private static ContextMenuEntry Entry(ContextMenuEntry item) => item with
    {
        Windows11ContextTypes = Copy(item.Windows11ContextTypes),
        Windows11Verbs = Copy(item.Windows11Verbs)
    };

    private static SpecialMenuEntry SpecialEntry(SpecialMenuEntry item) => item with
    {
        Metadata = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(item.Metadata))
    };

    private static DeletedItemsPurgeResult? Purge(DeletedItemsPurgeResult? result) => result is null ? null : result with
    {
        PurgedItems = Copy(result.PurgedItems),
        SkippedItemIds = Copy(result.SkippedItemIds),
        BackupCleanupFailedItemIds = Copy(result.BackupCleanupFailedItemIds)
    };

    private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> items) =>
        items.Count == 0 ? Array.Empty<T>() : Array.AsReadOnly(items.ToArray());

    private static IReadOnlyList<T> Transform<T>(IReadOnlyList<T> items, Func<T, T> clone) =>
        items.Count == 0 ? Array.Empty<T>() : Array.AsReadOnly(items.Select(clone).ToArray());
}
#endif
