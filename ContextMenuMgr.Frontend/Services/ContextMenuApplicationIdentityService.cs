using System.IO;
using System.Text.RegularExpressions;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.ViewModels;

namespace ContextMenuMgr.Frontend.Services;

public sealed class ContextMenuApplicationIdentityService
{
    private static readonly Regex CommandPathRegex = new(
        "^(?:\\\"(?<path>[^\\\"]+\\.(?:exe|dll))\\\"|(?<path>[^\\s]+\\.(?:exe|dll)))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ContextMenuApplicationIdentity GetIdentity(ContextMenuItemViewModel item)
        => GetIdentity(item.Entry);

    public ContextMenuApplicationIdentity GetIdentity(ContextMenuEntry entry)
    {
        var commandExecutablePath = ResolveCommandExecutablePath(entry);
        var handlerClsid = string.IsNullOrWhiteSpace(entry.HandlerClsid)
            ? null
            : entry.HandlerClsid.Trim();
        var identity = commandExecutablePath
            ?? handlerClsid
            ?? (!string.IsNullOrWhiteSpace(entry.RegistryPath) ? entry.RegistryPath.Trim() : entry.Id);

        return new ContextMenuApplicationIdentity(
            identity,
            commandExecutablePath,
            handlerClsid,
            entry.KeyName,
            entry.EntryKind);
    }

    public FileTypeBatchQuery CreateFileTypeBatchQuery(ContextMenuItemViewModel item)
    {
        var identity = GetIdentity(item);
        return new FileTypeBatchQuery
        {
            SourceItemId = item.Id,
            SourceDisplayName = item.DisplayName,
            EntryKind = identity.EntryKind,
            KeyName = identity.KeyName,
            CommandExecutablePath = identity.CommandExecutablePath,
            HandlerClsid = identity.HandlerClsid,
            SourceRegistryPath = item.Entry.RegistryPath,
            SourceBackendRegistryPath = item.Entry.BackendRegistryPath
        };
    }

    public bool CanBatchManageFileTypeItem(ContextMenuItemViewModel item)
    {
        if (item.IsDeleted
            || !item.IsPresentInRegistry
            || string.IsNullOrWhiteSpace(item.Entry.RegistryPath)
            || string.IsNullOrWhiteSpace(item.Entry.BackendRegistryPath))
        {
            return false;
        }

        var identity = GetIdentity(item);
        return item.Entry.EntryKind switch
        {
            ContextMenuEntryKind.ShellVerb => !string.IsNullOrWhiteSpace(identity.CommandExecutablePath)
                                              && !string.IsNullOrWhiteSpace(identity.KeyName),
            ContextMenuEntryKind.ShellExtension => !string.IsNullOrWhiteSpace(identity.HandlerClsid),
            _ => false
        };
    }

    /// <summary>
    /// Returns a compact, display-safe source label when an executable path is
    /// already present in entry metadata. This deliberately never falls back to
    /// a CLSID, registry path, or internal identity key.
    /// </summary>
    public static string? GetFriendlyApplicationName(ContextMenuEntry entry)
    {
        var executablePath = ResolveCommandExecutablePath(entry);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(executablePath.Trim());
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    private static string? ResolveCommandExecutablePath(ContextMenuEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.FilePath))
        {
            return entry.FilePath.Trim();
        }

        var commandMatch = CommandPathRegex.Match(entry.CommandText?.Trim() ?? string.Empty);
        return commandMatch.Success ? commandMatch.Groups["path"].Value.Trim() : null;
    }

}

public sealed record ContextMenuApplicationIdentity(
    string Identity,
    string? CommandExecutablePath,
    string? HandlerClsid,
    string KeyName,
    ContextMenuEntryKind EntryKind);
