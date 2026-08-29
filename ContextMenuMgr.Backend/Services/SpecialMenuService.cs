using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Catalogs and mutates special shell menu surfaces that are not normal shell verbs.
/// </summary>
public sealed class SpecialMenuService
{
    private const string ShellNewOrderPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew";
    private const string ShellNewLegacyBroadLockMessage = "The ShellNew order key is locked by a legacy broad ACL rule and cannot be safely unlocked without ownership repair. Use BluePointLilac/ContextMenuManager once to unlock it, then retry.";
    private const string CommandStorePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell";
    private const string GuidBlockedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string ApplicationsPath = @"Applications";
    private const string OpenWithPolicyPath = @"Software\Policies\Microsoft\Windows\Explorer";
    private const string IeRootPath = @"Software\Microsoft\Internet Explorer";
    private const string UserClassesPath = @"Software\Classes";
    private const string MachineClassesPath = @"SOFTWARE\Classes";
    private static readonly string DefaultSendToPath = Environment.ExpandEnvironmentVariables(@"%SystemDrive%\Users\Default\AppData\Roaming\Microsoft\Windows\SendTo");
    private static readonly string DefaultWinXPath = Environment.ExpandEnvironmentVariables(@"%SystemDrive%\Users\Default\AppData\Local\Microsoft\Windows\WinX");
    private const string DeletedSuffix = ".deleted";
    private const string DeletedFolderName = ".deleted";
    private const string BackupFolderName = ".backup";
    private const string WinXGlobalRestoreDisabledMessage = "Win+X global restore is disabled to avoid deleting the entire user Win+X folder. Restore a single group instead.";
    private const string WinXUndoDisabledMessage = "Win+X undo is currently disabled because Win+X items are deleted directly.";
    private const string WinXPurgeDeletedDisabledMessage = "Win+X deleted-item purge is disabled because Win+X items are deleted directly.";
    private const string DynamicSendToDriveIdPrefix = "sendto:drive:";
    // Do not use RegistryRights.WriteKey here: it is too broad and can block later ACL reads/unlock.
    // Do not add take-ownership fallback back into the main backend; it increases AV false-positive risk.
    private const RegistryRights ShellNewOrderLockRights = RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete;

    private readonly FileLogger _logger;

    public SpecialMenuService(FileLogger logger)
    {
        _logger = logger;
        WinXHasher.FileLoggerHost.Attach(logger);
    }

    public Task<IReadOnlyList<SpecialMenuEntry>> GetSnapshotAsync(SpecialMenuKind kind, BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        var requiresUserContext = kind is SpecialMenuKind.ShellNew or SpecialMenuKind.SendTo or SpecialMenuKind.WinX or SpecialMenuKind.OpenWith;
        if (kind == SpecialMenuKind.WinX)
        {
            var context = RequireUserContext(userContext);
            LogWinXOperation("Snapshot", context, GetWinXPath(context), GetWinXPath(context));
        }

        IReadOnlyList<SpecialMenuEntry> items = kind switch
        {
            SpecialMenuKind.ShellNew => GetShellNewItems(RequireUserContext(userContext)),
            SpecialMenuKind.SendTo => GetSendToItems(RequireUserContext(userContext)),
            SpecialMenuKind.WinX => GetWinXItems(RequireUserContext(userContext)),
            SpecialMenuKind.OpenWith => GetOpenWithItems(RequireUserContext(userContext)),
            SpecialMenuKind.DragDrop => GetDragDropItems(),
            SpecialMenuKind.CommandStore => GetCommandStoreItems(),
            SpecialMenuKind.GuidBlock => GetGuidBlockItems(),
            SpecialMenuKind.InternetExplorer => GetIeItems(),
            _ => []
        };

        _logger.LogFireAndForget($"SpecialMenuGetSnapshot: Kind={kind}, RequiresUserContext={requiresUserContext}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}, Count={items.Count}.");
        return Task.FromResult(items);
    }

    public async Task<PipeResponse> SetEnabledAsync(SpecialMenuEntry item, bool enabled, Guid? operationId, BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        try
        {
            ThrowIfReadOnlyPackagedShellNew(item);
            await _logger.LogAsync($"SpecialMenuSetEnabledStart: Kind={item.Kind}, Id={item.Id}, Enabled={enabled}, RegistryPath={item.RegistryPath}, Path={item.Path}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);
            if (item.Kind == SpecialMenuKind.WinX)
            {
                var context = RequireUserContext(userContext);
                LogWinXOperation("SetEnabled", context, item.Path ?? DecodeId(item.Id), GetWinXPath(context));
            }

            SpecialMenuEntry updated;
            try
            {
                if (item.Kind == SpecialMenuKind.SendTo && IsDynamicSendToDrive(item))
                {
                    throw new InvalidOperationException("Explorer-generated SendTo drive targets cannot be toggled.");
                }

                updated = item.Kind switch
                {
                    SpecialMenuKind.ShellNew => SetShellNewEnabled(item, enabled, RequireUserContext(userContext)),
                    SpecialMenuKind.SendTo => SetFileSystemItemEnabled(item, enabled, GetSendToPath(RequireUserContext(userContext))),
                    SpecialMenuKind.WinX => SetFileSystemItemEnabled(item, enabled, GetWinXPath(RequireUserContext(userContext))),
                    SpecialMenuKind.OpenWith => SetOpenWithEnabled(item, enabled, RequireUserContext(userContext)),
                    SpecialMenuKind.DragDrop => SetDragDropEnabled(item, enabled),
                    SpecialMenuKind.InternetExplorer => SetRenameBackedRegistryItemEnabled(item, enabled, "MenuExt", "-MenuExt"),
                    SpecialMenuKind.CommandStore => SetCommandStoreEnabled(item, enabled),
                    SpecialMenuKind.GuidBlock => SetGuidBlockEnabled(item, enabled),
                    _ => throw new InvalidOperationException("This special menu item cannot be toggled.")
                };
            }
            catch (UnauthorizedAccessException ex)
            {
                await _logger.LogAsync(RuntimeLogLevel.Warning, $"Permission denied when toggling {item.Kind} item {item.Id}: {ex}", cancellationToken);

                if (item.Kind == SpecialMenuKind.DragDrop)
                {
                    return Failure(
                        "Permission denied: Unable to modify DragDrop handler. " +
                        "This operation requires administrator privileges to modify HKEY_CLASSES_ROOT registry keys. " +
                        "Please run ContextMenuMgr as administrator or check the backend service permissions.",
                        operationId);
                }

                return Failure($"Permission denied: {ex.Message}", operationId);
            }
            catch (SecurityException ex)
            {
                await _logger.LogAsync(RuntimeLogLevel.Warning, $"Security exception when toggling {item.Kind} item {item.Id}: {ex}", cancellationToken);
                return Failure($"Security error: {ex.Message}. This operation may require elevated privileges.", operationId);
            }

            ShellChangeNotifier.NotifyAssociationsChanged();
            await _logger.LogAsync($"Set special menu item enabled. Kind={item.Kind}, Id={item.Id}, Enabled={enabled}, ResultId={updated.Id}, RegistryPath={updated.RegistryPath}, Path={updated.Path}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);
            return Success("Special menu item updated.", updated, operationId);
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Error, $"Failed to toggle special menu item {item.Id}: {ex}", cancellationToken);
            return Failure(ex.Message, operationId);
        }
    }

    public async Task<PipeResponse> CreateAsync(PipeRequest request, BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        try
        {
            await _logger.LogAsync($"SpecialMenuCreateStart: Kind={request.SpecialKind}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}, ShellNewExtension={request.ShellNewCreate?.Extension}, SendToName={request.SendToCreate?.DisplayName}, WinXGroup={request.WinXCreateGroup?.GroupName}, WinXEntry={request.WinXCreateEntry?.DisplayName}, OpenWithName={request.OpenWithCreate?.DisplayName}.", cancellationToken);
            if (request.SpecialKind == SpecialMenuKind.WinX)
            {
                var context = RequireUserContext(userContext);
                LogWinXOperation("Create", context, request.WinXCreateEntry?.TargetPath ?? request.WinXCreateGroup?.GroupName, GetWinXPath(context));
            }

            var item = request.SpecialKind switch
            {
                SpecialMenuKind.ShellNew when request.ShellNewCreate is not null => CreateShellNew(request.ShellNewCreate, RequireUserContext(userContext)),
                SpecialMenuKind.SendTo when request.SendToCreate is not null => CreateSendTo(request.SendToCreate, RequireUserContext(userContext)),
                SpecialMenuKind.WinX when request.WinXCreateGroup is not null => CreateWinXGroup(request.WinXCreateGroup, RequireUserContext(userContext)),
                SpecialMenuKind.WinX when request.WinXCreateEntry is not null => CreateWinXEntry(request.WinXCreateEntry, RequireUserContext(userContext)),
                SpecialMenuKind.OpenWith when request.OpenWithCreate is not null => CreateOpenWith(request.OpenWithCreate, RequireUserContext(userContext)),
                SpecialMenuKind.DragDrop when request.DragDropCreate is not null => CreateDragDrop(request.DragDropCreate),
                SpecialMenuKind.CommandStore when request.SpecialItem is not null => CreateCommandStore(request.SpecialItem),
                SpecialMenuKind.GuidBlock when request.GuidBlockCreate is not null => CreateGuidBlock(request.GuidBlockCreate),
                SpecialMenuKind.InternetExplorer when request.IeMenuCreate is not null => CreateIe(request.IeMenuCreate),
                _ => throw new InvalidOperationException("The create request was missing required data.")
            };

            ShellChangeNotifier.NotifyAssociationsChanged();
            await _logger.LogAsync($"Created special menu item. Kind={item.Kind}, Id={item.Id}, RegistryPath={item.RegistryPath}, Path={item.Path}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);
            return Success("Special menu item created.", item, request.ClientOperationId);
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Error, $"Failed to create special menu item: {ex}", cancellationToken);
            return Failure(ex.Message, request.ClientOperationId);
        }
    }

    public async Task<PipeResponse> UpdateAsync(PipeRequest request, BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        try
        {
            if (request.SpecialKind == SpecialMenuKind.ShellNew && request.ShellNewUpdate is not null)
            {
                ThrowIfReadOnlyPackagedShellNewId(request.ShellNewUpdate.Id);
            }

            await _logger.LogAsync($"SpecialMenuUpdateStart: Kind={request.SpecialKind}, TargetId={request.ShellNewUpdate?.Id ?? request.SpecialItem?.Id}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}, ChangedFields=ShellNew(DisplayName={request.ShellNewUpdate?.DisplayName is not null}, IconPath={request.ShellNewUpdate?.IconPath is not null}, Command={request.ShellNewUpdate?.Command is not null}, DataText={request.ShellNewUpdate?.DataText is not null}, BeforeSeparator={request.ShellNewUpdate?.BeforeSeparator is not null}).", cancellationToken);
            if (request.SpecialKind == SpecialMenuKind.WinX)
            {
                var context = RequireUserContext(userContext);
                LogWinXOperation("Update", context, request.WinXUpdateEntry?.Id, GetWinXPath(context));
            }

            var item = request.SpecialKind switch
            {
                SpecialMenuKind.ShellNew when request.ShellNewUpdate is not null => UpdateShellNew(request.ShellNewUpdate, RequireUserContext(userContext)),
                SpecialMenuKind.SendTo when request.SendToUpdate is not null => UpdateSendTo(request.SendToUpdate, RequireUserContext(userContext)),
                SpecialMenuKind.WinX when request.WinXUpdateEntry is not null => UpdateWinXEntry(request.WinXUpdateEntry, RequireUserContext(userContext)),
                SpecialMenuKind.OpenWith when request.OpenWithUpdate is not null => UpdateOpenWith(request.OpenWithUpdate, RequireUserContext(userContext)),
                SpecialMenuKind.DragDrop when request.DefaultDropEffect is not null => UpdateDefaultDropEffect(request.DefaultDropEffect.Value),
                SpecialMenuKind.InternetExplorer when request.IeMenuUpdate is not null => UpdateIe(request.IeMenuUpdate),
                SpecialMenuKind.CommandStore when request.SpecialItem is not null => UpdateCommandStore(request.SpecialItem),
                _ => throw new InvalidOperationException("The update request was missing required data.")
            };

            ShellChangeNotifier.NotifyAssociationsChanged();
            await _logger.LogAsync($"Updated special menu item. Kind={item.Kind}, Id={item.Id}, RegistryPath={item.RegistryPath}, Path={item.Path}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);
            return Success("Special menu item updated.", item, request.ClientOperationId);
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Error, $"Failed to update special menu item: {ex}", cancellationToken);
            return Failure(ex.Message, request.ClientOperationId);
        }
    }

    public async Task<PipeResponse> DeleteAsync(SpecialMenuEntry item, Guid? operationId, BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        try
        {
            ThrowIfReadOnlyPackagedShellNew(item);
            await _logger.LogAsync($"SpecialMenuDeleteStart: Kind={item.Kind}, Id={item.Id}, RegistryPath={item.RegistryPath}, Path={item.Path}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);
            string? deletedPath = null;
            var deletedAt = DateTime.Now.ToString("o");
            var metadata = new Dictionary<string, string>(item.Metadata) { ["IsDeleted"] = "true", ["DeletedAt"] = deletedAt };

            switch (item.Kind)
            {
                case SpecialMenuKind.InternetExplorer:
                    deletedPath = SoftDeleteRegistryTree(item.RegistryPath, logger: _logger);
                    if (item.Metadata.TryGetValue("DisabledRegistryPath", out var ieDisabledPath))
                    {
                        SoftDeleteRegistryTree(ieDisabledPath, logger: _logger);
                    }

                    break;
                case SpecialMenuKind.ShellNew:
                    deletedPath = SoftDeleteRegistryTree(item.RegistryPath, RequireUserContext(userContext), _logger);
                    if (item.Metadata.TryGetValue("DisabledRegistryPath", out var disabledPath))
                    {
                        SoftDeleteRegistryTree(disabledPath, RequireUserContext(userContext), _logger);
                    }

                    break;
                case SpecialMenuKind.DragDrop:
                case SpecialMenuKind.CommandStore:
                    deletedPath = SoftDeleteRegistryTree(item.RegistryPath, logger: _logger);
                    break;
                case SpecialMenuKind.OpenWith:
                    deletedPath = SoftDeleteRegistryTree(GetOpenWithItemAppPath(item), RequireUserContext(userContext), _logger);
                    break;
                case SpecialMenuKind.GuidBlock:
                    DeleteRegistryValue(Registry.LocalMachine, GuidBlockedPath, item.KeyName, _logger);
                    break;
                case SpecialMenuKind.SendTo:
                {
                    if (IsDynamicSendToDrive(item))
                    {
                        return Failure("Explorer-generated SendTo drive targets cannot be deleted.", operationId);
                    }

                    var result = SoftDeleteFileSystemItem(item.Path, GetSendToPath(RequireUserContext(userContext)), _logger);
                    if (!result.Success)
                    {
                        await _logger.LogAsync(RuntimeLogLevel.Error, $"DeleteSpecialMenuFileSystemSoftDeleteFailed: Kind={item.Kind}, Path={item.Path}, Message={result.Message}", cancellationToken);
                        return Failure(result.Message ?? "Soft delete failed.", operationId);
                    }

                    deletedPath = result.DeletedPath;
                    break;
                }
                case SpecialMenuKind.WinX:
                {
                    var context = RequireUserContext(userContext);
                    var winXPath = GetWinXPath(context);
                    var path = item.Path ?? DecodeId(item.Id);
                    LogWinXOperation("Delete", context, path, winXPath);
                    DeleteWinXItem(path, winXPath);
                    ShellChangeNotifier.NotifyAssociationsChanged();
                    await _logger.LogAsync($"Deleted Win+X item directly. Kind={item.Kind}, Id={item.Id}, Path={path}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);
                    return Success("Special menu item deleted.", null, operationId);
                }
            }

            ShellChangeNotifier.NotifyAssociationsChanged();
            await _logger.LogAsync($"Soft-deleted special menu item. Kind={item.Kind}, Id={item.Id}, SourcePath={item.RegistryPath ?? item.Path}, DeletedPath={deletedPath}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);

            if (!string.IsNullOrWhiteSpace(deletedPath))
            {
                metadata["DeletedPath"] = deletedPath;
            }

            var deletedItem = item with { Metadata = metadata };
            return Success("Special menu item deleted.", deletedItem, operationId);
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Error, $"Failed to delete special menu item {item.Id}: {ex}", cancellationToken);
            return Failure(ex.Message, operationId);
        }
    }

    public async Task<PipeResponse> UndoDeleteAsync(SpecialMenuEntry item, Guid? operationId, BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        try
        {
            ThrowIfReadOnlyPackagedShellNew(item);
            await _logger.LogAsync($"SpecialMenuUndoDeleteStart: Kind={item.Kind}, Id={item.Id}, RegistryPath={item.RegistryPath}, Path={item.Path}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);
            if (item.Kind == SpecialMenuKind.WinX)
            {
                return Failure(WinXUndoDisabledMessage, operationId);
            }

            switch (item.Kind)
            {
                case SpecialMenuKind.InternetExplorer:
                    RestoreSoftDeletedRegistryTree(item.RegistryPath, logger: _logger);
                    if (item.Metadata.TryGetValue("DisabledRegistryPath", out var ieDisabledPath))
                    {
                        RestoreSoftDeletedRegistryTree(ieDisabledPath, logger: _logger);
                    }

                    break;
                case SpecialMenuKind.ShellNew:
                    RestoreSoftDeletedRegistryTree(item.RegistryPath, RequireUserContext(userContext), _logger);
                    if (item.Metadata.TryGetValue("DisabledRegistryPath", out var disabledPath))
                    {
                        RestoreSoftDeletedRegistryTree(disabledPath, RequireUserContext(userContext), _logger);
                    }

                    break;
                case SpecialMenuKind.DragDrop:
                case SpecialMenuKind.CommandStore:
                    RestoreSoftDeletedRegistryTree(item.RegistryPath, logger: _logger);
                    break;
                case SpecialMenuKind.OpenWith:
                    RestoreSoftDeletedRegistryTree(
                        GetOpenWithItemAppPath(item),
                        RequireUserContext(userContext),
                        _logger,
                        item.Metadata.GetValueOrDefault("DeletedPath"));
                    break;
                case SpecialMenuKind.SendTo:
                    RestoreSoftDeletedFileSystemItem(item.Path, GetSendToPath(RequireUserContext(userContext)), item.Metadata.GetValueOrDefault("DeletedPath"), _logger);
                    break;
            }

            ShellChangeNotifier.NotifyAssociationsChanged();
            await _logger.LogAsync($"Restored soft-deleted special menu item. Kind={item.Kind}, Id={item.Id}, RestoredPath={item.RegistryPath ?? item.Path}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);

            var metadata = new Dictionary<string, string>(item.Metadata);
            metadata.Remove("IsDeleted");
            metadata.Remove("DeletedAt");
            var restoredItem = item with { Metadata = metadata };
            return Success("Special menu item restored.", restoredItem, operationId);
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Error, $"Failed to restore special menu item {item.Id}: {ex}", cancellationToken);
            return Failure(ex.Message, operationId);
        }
    }

    public async Task<PipeResponse> PurgeDeletedAsync(SpecialMenuEntry item, Guid? operationId, BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        try
        {
            ThrowIfReadOnlyPackagedShellNew(item);
            await _logger.LogAsync($"SpecialMenuPurgeDeletedStart: Kind={item.Kind}, Id={item.Id}, RegistryPath={item.RegistryPath}, Path={item.Path}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);
            if (item.Kind == SpecialMenuKind.WinX)
            {
                return Failure(WinXPurgeDeletedDisabledMessage, operationId);
            }

            switch (item.Kind)
            {
                case SpecialMenuKind.InternetExplorer:
                    DeleteRegistryTree(item.RegistryPath + DeletedSuffix, logger: _logger);
                    if (item.Metadata.TryGetValue("DisabledRegistryPath", out var ieDisabledPath))
                    {
                        DeleteRegistryTree(ieDisabledPath + DeletedSuffix, logger: _logger);
                    }

                    break;
                case SpecialMenuKind.ShellNew:
                    DeleteRegistryTree(item.RegistryPath + DeletedSuffix, RequireUserContext(userContext), _logger);
                    if (item.Metadata.TryGetValue("DisabledRegistryPath", out var disabledPath))
                    {
                        DeleteRegistryTree(disabledPath + DeletedSuffix, RequireUserContext(userContext), _logger);
                    }

                    break;
                case SpecialMenuKind.DragDrop:
                case SpecialMenuKind.CommandStore:
                    DeleteRegistryTree(item.RegistryPath + DeletedSuffix, logger: _logger);
                    break;
                case SpecialMenuKind.OpenWith:
                    DeleteRegistryTree(
                        item.Metadata.GetValueOrDefault("DeletedPath") ?? GetOpenWithItemAppPath(item) + DeletedSuffix,
                        RequireUserContext(userContext),
                        _logger);
                    break;
                case SpecialMenuKind.SendTo:
                {
                    var deletedPath = item.Metadata.GetValueOrDefault("DeletedPath");
                    if (!string.IsNullOrWhiteSpace(deletedPath))
                    {
                        DeleteFileSystemItem(deletedPath, Path.Combine(GetSendToPath(RequireUserContext(userContext)), DeletedFolderName));
                    }
                    else
                    {
                        DeleteFileSystemItem(item.Path, GetSendToPath(RequireUserContext(userContext)));
                    }

                    break;
                }
            }

            ShellChangeNotifier.NotifyAssociationsChanged();
            await _logger.LogAsync($"Permanently deleted special menu item. Kind={item.Kind}, Id={item.Id}, PurgedPath={item.RegistryPath ?? item.Path}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);
            return Success("Special menu item permanently deleted.", null, operationId);
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Error, $"Failed to permanently delete special menu item {item.Id}: {ex}", cancellationToken);
            return Failure(ex.Message, operationId);
        }
    }

    private static string? SoftDeleteRegistryTree(string? path, BackendUserContext? context = null, FileLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var deletedPath = path + DeletedSuffix;
            logger?.LogFireAndForget($"SoftDeleteRegistryTreeStart: Path={path}, DeletedPath={deletedPath}, Sid={DiagnosticLogFormatter.FormatSid(context)}.");
            using var sourceKey = OpenRegistryKey(path, writable: true, context);
            if (sourceKey is null)
            {
                logger?.LogFireAndForget($"SoftDeleteRegistryTreeSkipped: Path={path}, Reason=MissingSource, Sid={DiagnosticLogFormatter.FormatSid(context)}.");
                return null;
            }

            using var targetKey = CreateRegistryKey(deletedPath, context);
            if (targetKey is null)
            {
                return null;
            }

            CopyRegistryKey(sourceKey, targetKey);
            DeleteRegistryTree(path, context);
            logger?.LogFireAndForget($"SoftDeleteRegistryTreeEnd: Path={path}, DeletedPath={deletedPath}, Sid={DiagnosticLogFormatter.FormatSid(context)}, Result=Success.");
            return deletedPath;
        }
        catch (Exception ex)
        {
            logger?.LogFireAndForget(RuntimeLogLevel.Warning, $"SoftDeleteRegistryTreeFailed: Path={path}, Sid={DiagnosticLogFormatter.FormatSid(context)}, Exception={ex}");
            return null;
        }
    }

    private static void RestoreSoftDeletedRegistryTree(
        string? path,
        BackendUserContext? context = null,
        FileLogger? logger = null,
        string? deletedPathOverride = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var deletedPath = deletedPathOverride ?? path + DeletedSuffix;
        if (!IsDeletedRegistryPathFor(path, deletedPath))
        {
            logger?.LogFireAndForget(RuntimeLogLevel.Warning, $"RestoreSoftDeletedRegistryTreeSkipped: Path={path}, DeletedPath={deletedPath}, Reason=InvalidDeletedPath, Sid={DiagnosticLogFormatter.FormatSid(context)}.");
            return;
        }
        logger?.LogFireAndForget($"RestoreSoftDeletedRegistryTreeStart: Path={path}, DeletedPath={deletedPath}, Sid={DiagnosticLogFormatter.FormatSid(context)}.");
        using var sourceKey = OpenRegistryKey(deletedPath, writable: true, context);
        if (sourceKey is null)
        {
            logger?.LogFireAndForget($"RestoreSoftDeletedRegistryTreeSkipped: Path={path}, DeletedPath={deletedPath}, Reason=MissingDeletedSource, Sid={DiagnosticLogFormatter.FormatSid(context)}.");
            return;
        }

        using var targetKey = CreateRegistryKey(path, context);
        if (targetKey is null)
        {
            return;
        }

        CopyRegistryKey(sourceKey, targetKey);
        DeleteRegistryTree(deletedPath, context);
        logger?.LogFireAndForget($"RestoreSoftDeletedRegistryTreeEnd: Path={path}, DeletedPath={deletedPath}, Sid={DiagnosticLogFormatter.FormatSid(context)}, Result=Success.");
    }

    private sealed record FileSystemSoftDeleteResult(
        bool Success,
        bool SourceMissing,
        string? DeletedPath,
        string? Message);

    private static bool IsPathUnderDeletedFolder(string? path, string basePath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var deletedFolder = Path.Combine(basePath, DeletedFolderName);
        var fullPath = GetFullNormalizedPath(path);
        var fullRoot = GetFullNormalizedPath(deletedFolder);
        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
               || string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static FileSystemSoftDeleteResult SoftDeleteFileSystemItem(string? path, string basePath, FileLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new FileSystemSoftDeleteResult(false, false, null, "Path is null or empty.");
        }

        try
        {
            logger?.LogFireAndForget($"SoftDeleteFileSystemItemStart: Path={path}, BasePath={basePath}.");

            if (IsPathUnderDeletedFolder(path, basePath))
            {
                logger?.LogFireAndForget($"SoftDeleteFileSystemItemFailed: Path={path}, BasePath={basePath}, Reason=AlreadyUnderDeletedFolder.");
                return new FileSystemSoftDeleteResult(false, false, null, "The item is already in the deleted-items folder.");
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                logger?.LogFireAndForget($"SoftDeleteFileSystemItemSourceMissing: Path={path}, BasePath={basePath}.");
                return new FileSystemSoftDeleteResult(true, true, null, "Source already missing.");
            }

            var deletedFolder = Path.Combine(basePath, DeletedFolderName);
            Directory.CreateDirectory(deletedFolder);

            var fileName = Path.GetFileName(path);
            string deletedPath;

            if (File.Exists(path))
            {
                deletedPath = GetUniqueFilePath(Path.Combine(deletedFolder, fileName));
                logger?.LogFireAndForget($"SoftDeleteFileSystemItemTargetChosen: Path={path}, DeletedPath={deletedPath}.");
                File.Move(path, deletedPath, overwrite: false);
            }
            else if (Directory.Exists(path))
            {
                deletedPath = GetUniqueDirectoryPath(Path.Combine(deletedFolder, fileName));
                Directory.Move(path, deletedPath);
            }
            else
            {
                return new FileSystemSoftDeleteResult(false, false, null, "Source is not a file or directory.");
            }

            logger?.LogFireAndForget($"SoftDeleteFileSystemItemEnd: Path={path}, BasePath={basePath}, DeletedPath={deletedPath}, Result=Success.");
            return new FileSystemSoftDeleteResult(true, false, deletedPath, null);
        }
        catch (Exception ex)
        {
            logger?.LogFireAndForget(RuntimeLogLevel.Warning, $"SoftDeleteFileSystemItemFailed: Path={path}, BasePath={basePath}, Exception={ex}");
            return new FileSystemSoftDeleteResult(false, false, null, ex.Message);
        }
    }

    private static void RestoreSoftDeletedFileSystemItem(string? path, string basePath, string? deletedPathFromMetadata = null, FileLogger? logger = null)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return;
    }

    try
    {
        logger?.LogFireAndForget($"RestoreSoftDeletedFileSystemItemStart: Path={path}, BasePath={basePath}, DeletedPathFromMetadata={deletedPathFromMetadata}.");
        var deletedFolder = Path.Combine(basePath, DeletedFolderName);
        var fileName = Path.GetFileName(path);

        // Prefer the actual deleted path from metadata if available.
        var deletedPath = !string.IsNullOrWhiteSpace(deletedPathFromMetadata)
            ? deletedPathFromMetadata
            : Path.Combine(deletedFolder, fileName);

        if (!File.Exists(deletedPath) && !Directory.Exists(deletedPath))
        {
            logger?.LogFireAndForget($"RestoreSoftDeletedFileSystemItemSkipped: Path={path}, BasePath={basePath}, DeletedPath={deletedPath}, Reason=MissingDeletedItem.");
            return;
        }

        if (File.Exists(deletedPath))
        {
            var targetPath = GetUniqueFilePath(path);
            File.Move(deletedPath, targetPath, overwrite: false);
        }
        else if (Directory.Exists(deletedPath))
        {
            var targetPath = GetUniqueDirectoryPath(path);
            Directory.Move(deletedPath, targetPath);
        }

        logger?.LogFireAndForget($"RestoreSoftDeletedFileSystemItemEnd: OriginalPath={path}, BasePath={basePath}, DeletedPath={deletedPath}, Result=Success.");
        }
        catch (Exception ex)
        {
            logger?.LogFireAndForget(RuntimeLogLevel.Warning, $"RestoreSoftDeletedFileSystemItemFailed: Path={path}, BasePath={basePath}, Exception={ex}");
        }
    }

    public async Task<PipeResponse> MoveAsync(PipeRequest request, BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        try
        {
            SpecialMenuEntry item;
            if (request.ShellNewSort is not null)
            {
                item = await MoveShellNewAsync(request.ShellNewSort, RequireUserContext(userContext), cancellationToken);
            }
            else if (request.WinXMove is not null)
            {
                var context = RequireUserContext(userContext);
                LogWinXOperation("Move", context, request.WinXMove.Id, GetWinXPath(context));
                item = MoveWinX(request.WinXMove, context);
            }
            else
            {
                throw new InvalidOperationException("The move request was missing required data.");
            }

            ShellChangeNotifier.NotifyAssociationsChanged();
            await _logger.LogAsync($"Moved special menu item. Kind={item.Kind}, Id={item.Id}.", cancellationToken);
            return Success("Special menu item moved.", item, request.ClientOperationId);
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Error, $"Failed to move special menu item: {ex}", cancellationToken);
            return Failure(ex.Message, request.ClientOperationId);
        }
    }

    public async Task<PipeResponse> RestoreDefaultsAsync(SpecialMenuKind kind, string? groupName, Guid? operationId, BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        try
        {
            if (kind == SpecialMenuKind.SendTo)
            {
                var destination = GetSendToPath(RequireUserContext(userContext));
                await _logger.LogAsync($"RestoreDefaultsStart: Kind={kind}, Source={DefaultSendToPath}, Destination={destination}, GroupName={groupName}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);
                RestoreDirectory(DefaultSendToPath, destination);
            }
            else if (kind == SpecialMenuKind.WinX)
            {
                if (string.IsNullOrWhiteSpace(groupName))
                {
                    await _logger.LogAsync(RuntimeLogLevel.Warning, $"RestoreDefaultsRejected: Kind={kind}, Reason=GlobalWinXRestoreDisabled, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);
                    return Failure(WinXGlobalRestoreDisabledMessage, operationId);
                }

                var context = RequireUserContext(userContext);
                var validatedGroupName = ValidateWinXGroupName(groupName);
                var source = Path.Combine(DefaultWinXPath, validatedGroupName);
                var winXPath = GetWinXPath(context);
                var destination = Path.Combine(winXPath, validatedGroupName);
                await _logger.LogAsync($"RestoreDefaultsStart: Kind={kind}, Source={source}, Destination={destination}, GroupName={groupName}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);
                LogWinXOperation("RestoreGroupDefaults", context, destination, winXPath);
                EnsurePathUnder(destination, winXPath);
                RestoreWinXGroupWithBackup(source, destination, winXPath);
                foreach (var lnkPath in Directory.GetFiles(destination, "*.lnk", SearchOption.AllDirectories))
                {
                    await _logger.LogAsync($"WinXHashLnk: Path={lnkPath}.", cancellationToken);
                    var hashResult = WinXHasher.HashLnkWithResult(lnkPath);
                    if (!hashResult.Success)
                    {
                        throw new InvalidOperationException($"Win+X shortcut could not be hashed after restore. Path={lnkPath}, Error={hashResult.Error}");
                    }
                }
            }
            else
            {
                throw new InvalidOperationException("Defaults can only be restored for SendTo and Win+X.");
            }

            ShellChangeNotifier.NotifyAssociationsChanged();
            await _logger.LogAsync($"Restored defaults for {kind}. GroupName={groupName}, Sid={DiagnosticLogFormatter.FormatSid(userContext)}.", cancellationToken);
            return new PipeResponse { Success = true, Message = "Defaults restored.", ClientOperationId = operationId };
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Error, $"Failed to restore defaults for {kind}: {ex}", cancellationToken);
            return Failure(ex.Message, operationId);
        }
    }

    public async Task<PipeResponse> SetShellNewOrderLockAsync(bool locked, Guid? operationId, BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        try
        {
            var context = RequireUserContext(userContext);
            var fullPath = DiagnosticLogFormatter.FormatUserHivePath(context, ShellNewOrderPath);
            await _logger.LogAsync($"ShellNewOrderLockRequest: Sid={context.Sid}, FullPath={fullPath}, Locked={locked}.", cancellationToken);
            if (locked)
            {
                if (IsShellNewOrderLocked(context))
                {
                    var removed = RemoveShellNewOrderLock(context);
                    await _logger.LogAsync($"ShellNew order key was already locked before applying lock. Sid={context.Sid}, FullPath={fullPath}. {removed.Message}", cancellationToken);
                }

                var currentRealItems = GetShellNewItems(context).Where(IsClassicShellNewEntry).ToList();
                WriteShellNewOrderClasses(context, currentRealItems);
                var applied = ApplyShellNewOrderLock(context);
                await _logger.LogAsync($"ShellNew order lock applied. Sid={context.Sid}, FullPath={fullPath}, KeyCreated={applied.KeyCreated}. {applied.Message}", cancellationToken);
            }
            else
            {
                var removed = RemoveShellNewOrderLock(context);
                await _logger.LogAsync($"ShellNew order lock removed. Sid={context.Sid}, FullPath={fullPath}. {removed.Message}", cancellationToken);
            }

            await _logger.LogAsync($"ShellNew order lock set to {locked} for user {context.Sid}.", cancellationToken);
            return new PipeResponse { Success = true, Message = "ShellNew order lock updated successfully.", ClientOperationId = operationId };
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Error, $"SetShellNewOrderLockAsync failed: {ex}", cancellationToken);
            return Failure(ex.Message, operationId);
        }
    }

    private IReadOnlyList<SpecialMenuEntry> GetShellNewItems(BackendUserContext context)
    {
        var entries = new List<SpecialMenuEntry>();

        Dictionary<string, int> ordered;
        try
        {
            ordered = GetShellNewOrderedExtensions(context);
        }
        catch
        {
            ordered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var orderLocked = false;
        try
        {
            orderLocked = IsShellNewOrderLocked(context);
        }
        catch
        {
        }

        FrontendUserClassesRoot? mergedClasses = null;
        if (!FrontendUserClassesRoot.TryOpen(context, out mergedClasses, out var win32Error))
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning,
                $"ShellNewMergedClassesOpenFailed: Sid={context.Sid}, SessionId={context.SessionId?.ToString() ?? "<null>"}, Win32Error={win32Error}.");
        }
        var candidateCount = 0;
        using (mergedClasses)
        {
            if (mergedClasses is not null)
            {
                try
                {
                    var candidates = mergedClasses.ClassesRoot.GetSubKeyNames()
                        .Where(static name => name.StartsWith(".", StringComparison.Ordinal) || string.Equals(name, "Folder", StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(name => ordered.TryGetValue(name, out var index) ? index : int.MaxValue)
                        .ThenBy(static name => name, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    candidateCount = candidates.Length;

                    foreach (var extension in candidates)
                    {
                        try
                        {
                            foreach (var item in EnumerateShellNewForExtension(mergedClasses.ClassesRoot, extension, context))
                            {
                                entries.Add(item with
                                {
                                    Metadata = item.Metadata.Concat(new[]
                                    {
                                        new KeyValuePair<string, string>("OrderLocked", orderLocked.ToString())
                                    }).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                                });
                                break;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }

            AddPackagedShellNewItems(entries, context, mergedClasses?.ClassesRoot);
        }

        var userPhysical = entries.Count(item => string.Equals(item.Metadata.GetValueOrDefault("SourceScope"), "User", StringComparison.OrdinalIgnoreCase));
        var packaged = entries.Count(IsPackagedShellNewItem);
        _logger.LogFireAndForget($"ShellNewDiscovery: Sid={context.Sid}, CandidateExtensions={candidateCount}, EffectiveRegistrations={entries.Count - packaged}, Packaged={packaged}, UserPhysical={userPhysical}, MachinePhysical={entries.Count - packaged - userPhysical}, Ambiguous=0, SkippedInvalid=0.");
        return CreateShellNewPresentation(entries, ordered, orderLocked);
    }

    private static IReadOnlyList<SpecialMenuEntry> CreateShellNewPresentation(
        IReadOnlyList<SpecialMenuEntry> entries,
        IReadOnlyDictionary<string, int> ordered,
        bool orderLocked)
    {
        var sortableEntries = entries
            .OrderBy(item => ordered.TryGetValue(item.KeyName, out var index) ? index : int.MaxValue)
            .ThenBy(item => item.KeyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var beforeSeparator = sortableEntries.Where(static item => IsBeforeSeparatorEntry(item)).Select(static item => item with { CanMove = false }).ToArray();
        var afterSeparator = sortableEntries.Where(static item => !IsBeforeSeparatorEntry(item)).Select(item => item with { CanMove = item.CanEdit && orderLocked }).ToArray();
        return beforeSeparator
            .Concat(new[] { CreateShellNewSeparatorEntry() })
            .Concat(afterSeparator)
            .ToArray();
    }

    private void AddPackagedShellNewItems(List<SpecialMenuEntry> entries, BackendUserContext context, RegistryKey? mergedClassesRoot)
    {
        var classicExtensions = entries.Select(static item => item.KeyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in PackagedShellNewDiscovery.FindForUser(context.Sid, _logger)
                     .GroupBy(static item => item.Extension, StringComparer.OrdinalIgnoreCase)
                     .Select(static group => group.First()))
        {
            // A classic physical registration is already a manageable explanation for
            // this extension. Do not duplicate it with a package declaration.
            if (classicExtensions.Contains(declaration.Extension))
            {
                continue;
            }

            entries.Add(CreatePackagedShellNewEntry(declaration, mergedClassesRoot));
        }
    }

    private static SpecialMenuEntry CreatePackagedShellNewEntry(PackagedShellNewDeclaration declaration, RegistryKey? mergedClassesRoot)
    {
        var (displayName, iconPath, iconIndex) = ResolvePackagedShellNewPresentation(mergedClassesRoot, declaration.Extension);
        var metadata = new Dictionary<string, string>
        {
            ["ProviderType"] = PackagedShellNewDiscovery.ProviderType,
            ["Extension"] = declaration.Extension,
            ["PackageFullName"] = declaration.PackageFullName,
            ["PackageFamilyName"] = declaration.PackageFamilyName,
            ["PackageName"] = declaration.PackageName,
            ["ApplicationId"] = declaration.ApplicationId,
            ["ApplicationExecutable"] = declaration.ApplicationExecutable,
            ["ApplicationEntryPoint"] = declaration.ApplicationEntryPoint,
            ["FileTypeAssociationName"] = declaration.FileTypeAssociationName,
            ["ManifestPath"] = declaration.ManifestPath,
            ["ShellNewFileName"] = declaration.ShellNewFileName,
            ["ShellNewDisplayName"] = declaration.ShellNewDisplayName ?? string.Empty,
            ["ShellNewCommandParameters"] = declaration.ShellNewCommandParameters ?? string.Empty,
            ["FileTypeDeclaration"] = declaration.FileTypeDeclaration,
            ["CreationKind"] = "PackageManifest",
            ["BeforeSeparator"] = bool.FalseString,
            ["ReadOnlyReason"] = "PackageManifest"
        };

        return new SpecialMenuEntry
        {
            Id = $"shellnew:package:{Convert.ToBase64String(Encoding.UTF8.GetBytes($"{declaration.PackageFullName}|{declaration.ApplicationId}|{declaration.Extension}"))}",
            Kind = SpecialMenuKind.ShellNew,
            DisplayName = displayName,
            KeyName = declaration.Extension,
            IsEnabled = true,
            IconPath = iconPath,
            IconIndex = iconIndex,
            Path = declaration.PackageFamilyName,
            CanEdit = false,
            CanDelete = false,
            CanMove = false,
            Metadata = metadata
        };
    }

    private static (string DisplayName, string? IconPath, int IconIndex) ResolvePackagedShellNewPresentation(RegistryKey? mergedClassesRoot, string extension)
    {
        if (mergedClassesRoot is null)
        {
            return (extension, null, 0);
        }

        using var extensionKey = mergedClassesRoot.OpenSubKey(extension, writable: false);
        var progId = extensionKey?.GetValue(null)?.ToString();
        if (string.IsNullOrWhiteSpace(progId))
        {
            return (extension, null, 0);
        }

        using var progIdKey = mergedClassesRoot.OpenSubKey(progId, writable: false);
        var displayName = ShellMetadataResolver.ResolveResourceString(progIdKey?.GetValue("FriendlyTypeName")?.ToString());
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = ShellMetadataResolver.ResolveResourceString(progIdKey?.GetValue(null)?.ToString());
        }

        using var iconKey = mergedClassesRoot.OpenSubKey($@"{progId}\DefaultIcon", writable: false);
        var (iconPath, iconIndex) = ParseIconLocation(iconKey?.GetValue(null)?.ToString());
        return (string.IsNullOrWhiteSpace(displayName) ? extension : StripAcceleratorPrefix(displayName), iconPath, iconIndex);
    }

    private IEnumerable<SpecialMenuEntry> EnumerateShellNewForExtension(RegistryKey mergedClassesRoot, string extension, BackendUserContext context)
    {
        using var extensionKey = mergedClassesRoot.OpenSubKey(extension, writable: false);
        if (extensionKey is null)
        {
            yield break;
        }

        var progId = extensionKey.GetValue(null)?.ToString();
        var parts = new List<(string KeyPath, bool Enabled)>
        {
            ($@"{extension}\ShellNew", true),
            ($@"{extension}\-ShellNew", false)
        };

        if (!string.IsNullOrWhiteSpace(progId))
        {
            parts.Add(($@"{progId}\ShellNew", true));
            parts.Add(($@"{progId}\-ShellNew", false));
            // Older ContextMenuMgr builds probed this nonstandard shape. Keep it readable for compatibility.
            parts.Add(($@"{extension}\{progId}\ShellNew", true));
            parts.Add(($@"{extension}\{progId}\-ShellNew", false));
        }

        foreach (var part in parts)
        {
            using var effectiveShellNewKey = mergedClassesRoot.OpenSubKey(part.KeyPath, writable: false);
            if (effectiveShellNewKey is null || !HasAnyValue(effectiveShellNewKey, "NullFile", "Data", "FileName", "Directory", "Command"))
            {
                continue;
            }

            // Keep physical provenance in the same 64-bit view used to open the
            // Explorer-facing merged root. This also prevents a 32-bit service
            // binary from accidentally mutating WOW6432Node registrations.
            var classesView = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            using var usersRoot = RegistryKey.OpenBaseKey(RegistryHive.Users, classesView);
            using var machineRoot = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, classesView);
            using var userShellNewKey = usersRoot.OpenSubKey($@"{context.Sid}\{UserClassesPath}\{part.KeyPath}", writable: false);
            using var machineShellNewKey = machineRoot.OpenSubKey($@"{MachineClassesPath}\{part.KeyPath}", writable: false);
            var userProvidesEffectiveRegistration = userShellNewKey is not null && HasAnyValue(userShellNewKey, "NullFile", "Data", "FileName", "Directory", "Command");
            var machineProvidesEffectiveRegistration = machineShellNewKey is not null && HasAnyValue(machineShellNewKey, "NullFile", "Data", "FileName", "Directory", "Command");
            var physicalSource = ShellNewPhysicalSourceResolver.Resolve(
                effectiveRegistrationExists: true,
                userRegistrationExists: userProvidesEffectiveRegistration,
                machineRegistrationExists: machineProvidesEffectiveRegistration);
            if (physicalSource == ShellNewPhysicalSource.Unresolved)
            {
                _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"ShellNewPhysicalSourceUnresolved: Sid={context.Sid}, Extension={extension}, ProgId={progId}, LogicalShellNewPath={part.KeyPath}.");
                continue;
            }

            // The merged root is authoritative for visibility. For a duplicate physical
            // ShellNew key, HKCU takes precedence; otherwise the contributing HKLM key is
            // the mutation target. Never write the merged handle.
            var shellNewKey = physicalSource == ShellNewPhysicalSource.User ? userShellNewKey! : machineShellNewKey!;
            var effectivePrefix = physicalSource == ShellNewPhysicalSource.User
                ? $@"HKEY_USERS\{context.Sid}\{UserClassesPath}"
                : @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes";
            var effectiveSourceScope = physicalSource == ShellNewPhysicalSource.User ? "User" : "System";
            var displayName = ResolveShellNewDisplayName(mergedClassesRoot, effectiveShellNewKey, extension, progId);
            var (iconPath, iconIndex, iconFallback) = ResolveShellNewIcon(mergedClassesRoot, effectiveShellNewKey, extension, progId);
            var registryPath = $@"{effectivePrefix}\{part.KeyPath}";
            var metadata = new Dictionary<string, string>
            {
                ["Extension"] = extension,
                ["ProgId"] = progId ?? string.Empty,
                ["CreationKind"] = GetShellNewCreationKind(effectiveShellNewKey),
                ["BeforeSeparator"] = IsShellNewBeforeSeparator(effectiveShellNewKey, extension).ToString(),
                ["DisabledRegistryPath"] = $@"{effectivePrefix}\{GetSiblingShellNewPath(part.KeyPath, part.Enabled ? "-ShellNew" : "ShellNew")}",
                ["SourceScope"] = effectiveSourceScope
            };
            AddKnownShellNewLocalization(extension, metadata);
            yield return new SpecialMenuEntry
            {
                Id = EncodeId(SpecialMenuKind.ShellNew, registryPath),
                Kind = SpecialMenuKind.ShellNew,
                DisplayName = StripAcceleratorPrefix(string.IsNullOrWhiteSpace(displayName) ? extension : displayName),
                KeyName = extension,
                IsEnabled = part.Enabled,
                IconPath = iconPath,
                IconIndex = iconIndex,
                RegistryPath = registryPath,
                CommandText = effectiveShellNewKey.GetValue("Command")?.ToString(),
                TargetPath = iconFallback,
                CanMove = true,
                Notes = progId,
                Metadata = metadata
            };
        }
    }

    private SpecialMenuEntry CreateShellNew(ShellNewCreateRequest request, BackendUserContext context)
    {
        var extension = NormalizeExtension(request.Extension);
        if (extension == ".")
        {
            throw new InvalidOperationException("The extension must include a file type, for example .txt.");
        }

        _logger.LogFireAndForget($"CreateShellNewStart: ShellNewCreateFixVersion=RelaxExtensionRegistration-v2, Sid={context.Sid}, Extension={extension}.");
        var specs = GetClassesRootSpecs(context);
        var extensionExists = ShellNewExtensionExists(extension, specs);
        var progIdResolution = ResolveShellNewProgId(extension, context, specs);
        _logger.LogFireAndForget($"CreateShellNewStart: Sid={context.Sid}, Extension={extension}, ExtensionExists={extensionExists}, ResolvedProgId={progIdResolution?.ProgId}, ProgIdSource={progIdResolution?.Source}, UserClassesPath={DiagnosticLogFormatter.FormatUserHivePath(context, UserClassesPath)}.");
        if (!extensionExists)
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"CreateShellNewExtensionNotRegisteredWarning: Sid={context.Sid}, Extension={extension}, Action=CreateUserClassesExtensionShellNew.");
        }

        if (progIdResolution is null)
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"CreateShellNewNoProgIdWarning: Sid={context.Sid}, Extension={extension}, Action=ContinueWithExtensionLevelShellNew.");
        }

        using var userClasses = GetWritableUserClassesRoot(context);
        using var extensionKey = userClasses.CreateSubKey(extension, writable: true)
            ?? throw new InvalidOperationException($"Unable to create HKEY_USERS\\{context.Sid}\\{UserClassesPath}\\{extension}. Check registry permissions.");
        var currentProgId = extensionKey.GetValue(null)?.ToString();
        if (string.IsNullOrWhiteSpace(currentProgId)
            && progIdResolution is not null
            && progIdResolution.SourceKind != ShellNewProgIdSourceKind.UserChoice)
        {
            extensionKey.SetValue(null, progIdResolution.ProgId, RegistryValueKind.String);
            _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("CreateShellNew", $@"HKEY_USERS\{context.Sid}\{UserClassesPath}\{extension}", null, RegistryValueKind.String, progIdResolution.ProgId, writable: true, result: "SetValue Success"));
            currentProgId = progIdResolution.ProgId;
        }

        using var shellNewKey = extensionKey.CreateSubKey("ShellNew", writable: true)
            ?? throw new InvalidOperationException($"Unable to create HKEY_USERS\\{context.Sid}\\{UserClassesPath}\\{extension}\\ShellNew. Check registry permissions.");
        var shellNewPath = $@"HKEY_USERS\{context.Sid}\{UserClassesPath}\{extension}\ShellNew";
        var wroteValues = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Command))
        {
            shellNewKey.SetValue("Command", request.Command, RegistryValueKind.String);
            shellNewKey.DeleteValue("NullFile", throwOnMissingValue: false);
            shellNewKey.DeleteValue("Data", throwOnMissingValue: false);
            wroteValues.Add("Command");
            _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("CreateShellNew", shellNewPath, "Command", RegistryValueKind.String, request.Command, writable: true, result: "SetValue Success"));
        }
        else if (request.DataText is not null)
        {
            var data = Encoding.UTF8.GetBytes(request.DataText);
            shellNewKey.SetValue("Data", data, RegistryValueKind.Binary);
            shellNewKey.DeleteValue("NullFile", throwOnMissingValue: false);
            wroteValues.Add("Data");
            _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("CreateShellNew", shellNewPath, "Data", RegistryValueKind.Binary, data, writable: true, result: "SetValue Success"));
        }
        else
        {
            shellNewKey.SetValue("NullFile", string.Empty, RegistryValueKind.String);
            shellNewKey.DeleteValue("Command", throwOnMissingValue: false);
            shellNewKey.DeleteValue("Data", throwOnMissingValue: false);
            wroteValues.Add("NullFile");
            _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("CreateShellNew", shellNewPath, "NullFile", RegistryValueKind.String, string.Empty, writable: true, result: "SetValue Success"));
        }

        if (!string.IsNullOrWhiteSpace(request.IconPath))
        {
            shellNewKey.SetValue("IconPath", request.IconPath, RegistryValueKind.String);
            wroteValues.Add("IconPath");
            _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("CreateShellNew", shellNewPath, "IconPath", RegistryValueKind.String, request.IconPath, writable: true, result: "SetValue Success"));
        }

        var friendlyProgId = !string.IsNullOrWhiteSpace(currentProgId) ? currentProgId : progIdResolution?.ProgId;
        if (!string.IsNullOrWhiteSpace(request.DisplayName) && !string.IsNullOrWhiteSpace(friendlyProgId))
        {
            using var progIdKey = GetWritableUserClassesSubKey(context, friendlyProgId, create: true);
            progIdKey?.SetValue("FriendlyTypeName", request.DisplayName, RegistryValueKind.String);
            wroteValues.Add("FriendlyTypeName");
            _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("CreateShellNew", $@"HKEY_USERS\{context.Sid}\{UserClassesPath}\{friendlyProgId}", "FriendlyTypeName", RegistryValueKind.String, request.DisplayName, writable: true, result: "SetValue Success"));
        }
        else if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"CreateShellNewDisplayNameWarning: Sid={context.Sid}, Extension={extension}, Reason=NoValidProgIdForFriendlyTypeName, Action=CreatedShellNewWithoutPlainMenuText.");
        }

        if (request.BeforeSeparator)
        {
            using var config = shellNewKey.CreateSubKey("Config", writable: true);
            config?.SetValue("BeforeSeparator", string.Empty, RegistryValueKind.String);
            wroteValues.Add("Config\\BeforeSeparator");
            _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("CreateShellNew", $@"{shellNewPath}\Config", "BeforeSeparator", RegistryValueKind.String, string.Empty, writable: true, result: "SetValue Success"));
        }

        _logger.LogFireAndForget($"CreateShellNewWriteValues: Sid={context.Sid}, ShellNewPath={shellNewPath}, Values={string.Join(";", wroteValues)}.");
        var item = GetShellNewItems(context)
            .Where(IsClassicShellNewEntry)
            .FirstOrDefault(entry => string.Equals(entry.RegistryPath, shellNewPath, StringComparison.OrdinalIgnoreCase))
            ?? CreateSyntheticShellNewEntry(context, extension, request, progIdResolution?.ProgId, shellNewPath);
        item = ApplyShellNewCreateRequestToReturnedItem(item, request, displayNamePersisted: !string.IsNullOrWhiteSpace(request.DisplayName) && !string.IsNullOrWhiteSpace(friendlyProgId));
        _logger.LogFireAndForget($"CreateShellNewEnd: Sid={context.Sid}, Extension={extension}, CreatedKeyPath={item.RegistryPath}, ResultItemId={item.Id}, ResultPath={item.RegistryPath}.");
        return item;
    }

    private static bool ShellNewExtensionExists(string extension, IReadOnlyList<ClassesRootSpec> specs)
    {
        foreach (var spec in specs)
        {
            using var key = spec.Root.OpenSubKey(extension, writable: false);
            if (key is not null)
            {
                return true;
            }
        }

        return false;
    }

    private ShellNewProgIdResolution? ResolveShellNewProgId(string extension, BackendUserContext context, IReadOnlyList<ClassesRootSpec> specs)
    {
        foreach (var spec in specs.Where(static item => string.Equals(item.SourceScope, "User", StringComparison.OrdinalIgnoreCase)))
        {
            using var extensionKey = spec.Root.OpenSubKey(extension, writable: false);
            var candidate = extensionKey?.GetValue(null)?.ToString();
            if (IsValidShellNewProgId(candidate, specs))
            {
                return new ShellNewProgIdResolution(candidate!, "UserExtensionDefault", ShellNewProgIdSourceKind.ExtensionDefault);
            }
        }

        foreach (var spec in specs.Where(static item => string.Equals(item.SourceScope, "System", StringComparison.OrdinalIgnoreCase)))
        {
            using var extensionKey = spec.Root.OpenSubKey(extension, writable: false);
            var candidate = extensionKey?.GetValue(null)?.ToString();
            if (IsValidShellNewProgId(candidate, specs))
            {
                return new ShellNewProgIdResolution(candidate!, "MachineExtensionDefault", ShellNewProgIdSourceKind.ExtensionDefault);
            }
        }

        var userChoiceProgId = ReadUserChoiceProgId(extension, context);
        if (IsValidShellNewProgId(userChoiceProgId, specs))
        {
            return new ShellNewProgIdResolution(userChoiceProgId!, "UserChoice", ShellNewProgIdSourceKind.UserChoice);
        }

        if (!string.IsNullOrWhiteSpace(userChoiceProgId))
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"CreateShellNewUserChoiceInvalidProgId: Sid={context.Sid}, Extension={extension}, ProgId={userChoiceProgId}.");
        }

        foreach (var spec in specs)
        {
            using var extensionKey = spec.Root.OpenSubKey(extension, writable: false);
            using var openWithProgids = extensionKey?.OpenSubKey("OpenWithProgids", writable: false);
            if (openWithProgids is null)
            {
                continue;
            }

            foreach (var candidate in openWithProgids.GetValueNames().Where(static name => !string.IsNullOrWhiteSpace(name)))
            {
                if (IsValidShellNewProgId(candidate, specs))
                {
                    return new ShellNewProgIdResolution(candidate, $"OpenWithProgids:{spec.SourceScope}", ShellNewProgIdSourceKind.OpenWithProgids);
                }
            }
        }

        return null;
    }

    private static string? ReadUserChoiceProgId(string extension, BackendUserContext context)
    {
        using var userRoot = GetUserRegistryRoot(context, writable: false);
        using var userChoice = userRoot.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{extension}\UserChoice", writable: false);
        return userChoice?.GetValue("ProgId")?.ToString();
    }

    private static bool IsValidShellNewProgId(string? progId, IReadOnlyList<ClassesRootSpec> specs)
    {
        if (string.IsNullOrWhiteSpace(progId))
        {
            return false;
        }

        foreach (var spec in specs)
        {
            using var key = spec.Root.OpenSubKey(progId, writable: false);
            if (key is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static SpecialMenuEntry CreateSyntheticShellNewEntry(
        BackendUserContext context,
        string extension,
        ShellNewCreateRequest request,
        string? progId,
        string shellNewPath)
    {
        var (iconPath, iconIndex) = ParseIconLocation(request.IconPath);
        var beforeSeparator = request.BeforeSeparator.ToString();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Extension"] = extension,
            ["ProgId"] = progId ?? string.Empty,
            ["CreationKind"] = !string.IsNullOrWhiteSpace(request.Command) ? "Command" : request.DataText is not null ? "Data" : "NullFile",
            ["BeforeSeparator"] = beforeSeparator,
            ["DisabledRegistryPath"] = $@"HKEY_USERS\{context.Sid}\{UserClassesPath}\{extension}\-ShellNew",
            ["SourceScope"] = "User"
        };
        AddKnownShellNewLocalization(extension, metadata);
        return new SpecialMenuEntry
        {
            Id = EncodeId(SpecialMenuKind.ShellNew, shellNewPath),
            Kind = SpecialMenuKind.ShellNew,
            DisplayName = StripAcceleratorPrefix(string.IsNullOrWhiteSpace(request.DisplayName) ? extension : request.DisplayName),
            KeyName = extension,
            IsEnabled = true,
            IconPath = iconPath,
            IconIndex = iconIndex,
            RegistryPath = shellNewPath,
            CommandText = request.Command,
            TargetPath = extension,
            CanMove = true,
            Notes = progId,
            Metadata = metadata
        };
    }

    private static SpecialMenuEntry ApplyShellNewCreateRequestToReturnedItem(SpecialMenuEntry item, ShellNewCreateRequest request, bool displayNamePersisted)
    {
        var result = item;
        if (displayNamePersisted && !string.IsNullOrWhiteSpace(request.DisplayName))
        {
            result = result with { DisplayName = StripAcceleratorPrefix(request.DisplayName) };
        }

        if (!string.IsNullOrWhiteSpace(request.IconPath))
        {
            var (iconPath, iconIndex) = ParseIconLocation(request.IconPath);
            result = result with { IconPath = iconPath, IconIndex = iconIndex };
        }

        if (!string.IsNullOrWhiteSpace(request.Command))
        {
            result = result with { CommandText = request.Command };
        }

        return result;
    }

    private SpecialMenuEntry UpdateShellNew(ShellNewUpdateRequest request, BackendUserContext context)
    {
        var registryPath = DecodeId(request.Id);
        var existingItem = GetShellNewItems(context)
            .Where(IsClassicShellNewEntry)
            .FirstOrDefault(item => string.Equals(item.RegistryPath, registryPath, StringComparison.OrdinalIgnoreCase));
        var sourceScope = existingItem?.Metadata.TryGetValue("SourceScope", out var itemScope) == true ? itemScope : "Unknown";
        var extension = existingItem?.Metadata.TryGetValue("Extension", out var itemExtension) == true
            ? itemExtension
            : GetShellNewExtensionFromRegistryPath(registryPath, context);
        var specs = GetClassesRootSpecs(context);
        var progIdResolution = !string.IsNullOrWhiteSpace(extension)
            ? ResolveShellNewProgId(extension, context, specs)
            : null;
        var progId = progIdResolution?.ProgId;
        using var key = OpenRegistryKey(registryPath, writable: true, context)
            ?? throw new InvalidOperationException($"Unable to open discovered physical ShellNew registration {registryPath} for update.");
        _logger.LogFireAndForget($"UpdateShellNewStart: Sid={context.Sid}, OriginalRegistryPath={registryPath}, SourceScope={sourceScope}, FinalRegistryPath={registryPath}, Extension={extension}, ProgId={progId}, ProgIdSource={progIdResolution?.Source}, TouchFriendlyTypeName={request.DisplayName is not null}, TouchIconPath={request.IconPath is not null}, TouchCommand={request.Command is not null}, TouchData={request.DataText is not null}, TouchBeforeSeparator={request.BeforeSeparator is not null}.");

        var menuTextDeleted = false;
        string? displayNameWriteTarget = null;
        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            if (string.IsNullOrWhiteSpace(progId))
            {
                throw new InvalidOperationException("This ShellNew item has no valid default file type association, so its display name cannot be changed safely.");
            }

            menuTextDeleted = key.GetValue("MenuText") is not null;
            key.DeleteValue("MenuText", throwOnMissingValue: false);
            using var progIdKey = GetWritableUserClassesSubKey(context, progId, create: true)
                ?? throw new InvalidOperationException($"Unable to create HKEY_USERS\\{context.Sid}\\{UserClassesPath}\\{progId}. Check registry permissions.");
            progIdKey.SetValue("FriendlyTypeName", request.DisplayName, RegistryValueKind.String);
            displayNameWriteTarget = $@"HKEY_USERS\{context.Sid}\{UserClassesPath}\{progId}";
            _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("UpdateShellNew", displayNameWriteTarget, "FriendlyTypeName", RegistryValueKind.String, request.DisplayName, writable: true, result: "SetValue Success"));
        }

        SetOptionalValue(key, "IconPath", request.IconPath);
        _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("UpdateShellNew", registryPath, "IconPath", RegistryValueKind.String, request.IconPath, writable: true, result: request.IconPath is null ? "DeleteValue/Skipped" : "SetValue"));
        SetOptionalValue(key, "Command", request.Command);
        _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("UpdateShellNew", registryPath, "Command", RegistryValueKind.String, request.Command, writable: true, result: request.Command is null ? "DeleteValue/Skipped" : "SetValue"));
        if (request.DataText is not null)
        {
            key.DeleteValue("NullFile", throwOnMissingValue: false);
            var data = Encoding.UTF8.GetBytes(request.DataText);
            key.SetValue("Data", data, RegistryValueKind.Binary);
            _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("UpdateShellNew", registryPath, "NullFile", RegistryValueKind.String, null, writable: true, result: "DeleteValue"));
            _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("UpdateShellNew", registryPath, "Data", RegistryValueKind.Binary, data, writable: true, result: "SetValue"));
        }

        if (request.BeforeSeparator is not null)
        {
            using var config = request.BeforeSeparator.Value
                ? key.CreateSubKey("Config", writable: true)
                : key.OpenSubKey("Config", writable: true);
            if (request.BeforeSeparator.Value)
            {
                config?.SetValue("BeforeSeparator", string.Empty, RegistryValueKind.String);
                _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("UpdateShellNew", $@"{registryPath}\Config", "BeforeSeparator", RegistryValueKind.String, string.Empty, writable: true, result: "SetValue"));
            }
            else
            {
                config?.DeleteValue("BeforeSeparator", throwOnMissingValue: false);
                _logger.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("UpdateShellNew", $@"{registryPath}\Config", "BeforeSeparator", RegistryValueKind.String, null, writable: true, result: "DeleteValue"));
            }
        }

        var item = GetShellNewItems(context).First(entry => string.Equals(entry.RegistryPath, registryPath, StringComparison.OrdinalIgnoreCase) || string.Equals(entry.KeyName, extension, StringComparison.OrdinalIgnoreCase));
        _logger.LogFireAndForget($"UpdateShellNewEnd: Sid={context.Sid}, OriginalRegistryPath={registryPath}, SourceScope={sourceScope}, Extension={extension}, ResolvedProgId={progId}, DisplayNameWriteTarget={displayNameWriteTarget}, MenuTextDeleted={menuTextDeleted}, FinalRegistryPath={registryPath}, ResultId={item.Id}, ResultPath={item.RegistryPath}.");
        return item;
    }

    internal SpecialMenuEntry SetShellNewEnabled(SpecialMenuEntry item, bool enabled, BackendUserContext context)
    {
        var declaredPath = item.RegistryPath ?? DecodeId(item.Id);
        var (enabledPath, disabledPath) = GetShellNewPhysicalPaths(declaredPath);
        var enabledExists = RegistryKeyExists(enabledPath, context);
        var disabledExists = RegistryKeyExists(disabledPath, context);

        if (enabledExists == disabledExists)
        {
            throw enabledExists
                ? new InvalidOperationException($"Both active and disabled ShellNew registrations exist: {enabledPath} and {disabledPath}. Resolve the conflicting registrations before toggling.")
                : new InvalidOperationException($"The ShellNew registration no longer exists at either expected path: {enabledPath} or {disabledPath}.");
        }

        var actualSource = enabledExists ? enabledPath : disabledPath;
        var actualTarget = enabled ? enabledPath : disabledPath;
        if (!string.Equals(actualSource, actualTarget, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogFireAndForget($"SetShellNewEnabledMoveStart: Sid={context.Sid}, Enabled={enabled}, DeclaredPath={declaredPath}, SourcePath={actualSource}, TargetPath={actualTarget}.");
            MoveRegistryKey(actualSource, actualTarget, context, _logger, requireTargetAbsent: true);
            if (RegistryKeyExists(actualSource, context) || !RegistryKeyExists(actualTarget, context))
            {
                throw new InvalidOperationException($"ShellNew registry move verification failed. Expected source to be absent ({actualSource}) and target to exist ({actualTarget}).");
            }

            _logger.LogFireAndForget($"SetShellNewEnabledMoveEnd: Sid={context.Sid}, Enabled={enabled}, SourcePath={actualSource}, TargetPath={actualTarget}, Result=VerifiedSuccess.");
        }

        var metadata = new Dictionary<string, string>(item.Metadata)
        {
            // Despite its historical name, this is the alternate physical path.
            ["DisabledRegistryPath"] = enabled ? disabledPath : enabledPath
        };
        return item with
        {
            Id = EncodeId(SpecialMenuKind.ShellNew, actualTarget),
            IsEnabled = enabled,
            RegistryPath = actualTarget,
            Metadata = metadata
        };
    }

    private static (string EnabledPath, string DisabledPath) GetShellNewPhysicalPaths(string path)
    {
        var normalized = path.Trim().Replace('/', '\\').TrimEnd('\\');
        if (normalized.EndsWith(@"\ShellNew", StringComparison.OrdinalIgnoreCase))
        {
            return (normalized, normalized[..^"ShellNew".Length] + "-ShellNew");
        }

        if (normalized.EndsWith(@"\-ShellNew", StringComparison.OrdinalIgnoreCase))
        {
            return (normalized[..^"-ShellNew".Length] + "ShellNew", normalized);
        }

        throw new InvalidOperationException($"ShellNew registration path must end in \\ShellNew or \\-ShellNew: {path}");
    }

    private async Task<SpecialMenuEntry> MoveShellNewAsync(ShellNewSortRequest request, BackendUserContext context, CancellationToken cancellationToken)
    {
        if (!IsShellNewOrderLocked(context))
        {
            throw new InvalidOperationException("ShellNew ordering requires the new-menu lock to be enabled first.");
        }

        var registryPath = DecodeId(request.Id);
        var extension = registryPath.Split('\\').FirstOrDefault(static part => part.StartsWith('.')) ?? string.Empty;
        ThrowIfReadOnlyPackagedShellNewId(request.Id);
        var allRealItems = GetShellNewItems(context).Where(IsClassicShellNewEntry).ToList();
        var movableItems = allRealItems.Where(static item => item.CanMove).ToList();
        await _logger.LogAsync(
            $"MoveShellNewStart: Sid={context.Sid}, OrderLocked=true, RequestId={request.Id}, Extension={extension}, MoveUp={request.MoveUp}, AllRealItemsBefore={JoinShellNewOrder(allRealItems)}, MovableItemsBefore={JoinShellNewOrder(movableItems)}.",
            cancellationToken);
        var index = movableItems.FindIndex(item => string.Equals(item.KeyName, extension, StringComparison.OrdinalIgnoreCase));
        var target = request.MoveUp ? index - 1 : index + 1;
        if (index < 0 || target < 0 || target >= movableItems.Count)
        {
            return movableItems.ElementAtOrDefault(index) ?? throw new InvalidOperationException("ShellNew item was not found.");
        }

        var sourceAllIndex = allRealItems.FindIndex(item => string.Equals(item.Id, movableItems[index].Id, StringComparison.OrdinalIgnoreCase));
        var targetAllIndex = allRealItems.FindIndex(item => string.Equals(item.Id, movableItems[target].Id, StringComparison.OrdinalIgnoreCase));
        if (sourceAllIndex < 0 || targetAllIndex < 0)
        {
            throw new InvalidOperationException("ShellNew item was not found.");
        }

        await _logger.LogAsync($"MoveShellNewSwap: Sid={context.Sid}, SourceIndex={index}, TargetIndex={target}, SourceAllIndex={sourceAllIndex}, TargetAllIndex={targetAllIndex}.", cancellationToken);
        (allRealItems[sourceAllIndex], allRealItems[targetAllIndex]) = (allRealItems[targetAllIndex], allRealItems[sourceAllIndex]);
        var movedItem = allRealItems[targetAllIndex];

        var unlocked = false;
        try
        {
            var unlock = RemoveShellNewOrderLock(context);
            unlocked = true;
            await _logger.LogAsync($"ShellNew move unlocked order key. UnlockResult={unlock.Message}", cancellationToken);
            Exception? writeFailure = null;
            try
            {
                WriteShellNewOrderClasses(context, allRealItems);
                await _logger.LogAsync($"ShellNew move write Classes result. Result=Success, FinalClasses={JoinShellNewOrder(allRealItems)}.", cancellationToken);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or InvalidOperationException)
            {
                writeFailure = ex;
                await _logger.LogAsync(RuntimeLogLevel.Warning, $"ShellNew order write failed after simple unlock. No ACL reset or ownership fallback will be attempted. Sid={context.Sid}, Path={DiagnosticLogFormatter.FormatUserHivePath(context, ShellNewOrderPath)}, Error={ex}", cancellationToken);
            }

            if (writeFailure is not null)
            {
                throw new InvalidOperationException(
                    "ShellNew order move failed after unlocking with ChangePermissions only. The ShellNew order ACL may need external repair.",
                    writeFailure);
            }
        }
        finally
        {
            if (unlocked)
            {
                var relock = ApplyShellNewOrderLock(context);
                await _logger.LogAsync($"ShellNew move relocked order key. RelockResult=Success, KeyCreated={relock.KeyCreated}. {relock.Message}", cancellationToken);
            }
        }

        await _logger.LogAsync($"MoveShellNewEnd: Sid={context.Sid}, Extension={extension}, FinalClasses={JoinShellNewOrder(allRealItems)}, MovedItemId={movedItem.Id}.", cancellationToken);

        return movedItem;
    }

    private static string JoinShellNewOrder(IEnumerable<SpecialMenuEntry> items)
        => string.Join(";", items.Select(static item => item.KeyName));

    private static IReadOnlyList<SpecialMenuEntry> GetSendToItems(BackendUserContext context)
    {
        var sendToPath = GetSendToPath(context);
        if (string.IsNullOrWhiteSpace(sendToPath))
        {
            return [];
        }

        Directory.CreateDirectory(sendToPath);
        var directoryItems = Directory.EnumerateFileSystemEntries(sendToPath)
            .Where(static path => !string.Equals(Path.GetFileName(path), "desktop.ini", StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(Path.GetFileName(path), DeletedFolderName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => DesktopIniStore.GetLocalizedFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(CreateSendToEntry);

        return directoryItems
            .Concat(GetDynamicSendToDriveItems())
            .ToArray();
    }

    private static SpecialMenuEntry CreateSendTo(SendToCreateRequest request, BackendUserContext context)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.TargetPath))
        {
            throw new InvalidOperationException("SendTo items require a display name and target path.");
        }

        var sendToPath = GetSendToPath(context);
        Directory.CreateDirectory(sendToPath);
        var path = GetUniqueFilePath(Path.Combine(sendToPath, RemoveIllegalChars(request.DisplayName) + ".lnk"));
        ShortcutFile.Write(path, request.TargetPath, request.Arguments, request.WorkingDirectory, request.DisplayName, null, null);
        DesktopIniStore.SetLocalizedFileName(path, request.DisplayName);
        return CreateSendToEntry(path);
    }

    private static SpecialMenuEntry UpdateSendTo(SendToUpdateRequest request, BackendUserContext context)
    {
        if (IsDynamicSendToDriveId(request.Id))
        {
            throw new InvalidOperationException("Explorer-generated SendTo drive targets cannot be edited.");
        }

        var path = DecodeId(request.Id);
        EnsurePathUnder(path, GetSendToPath(context));
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new InvalidOperationException("The SendTo item no longer exists.");
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            DesktopIniStore.SetLocalizedFileName(path, request.DisplayName);
        }

        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var current = ShortcutFile.Read(path);
            ShortcutFile.Write(
                path,
                request.TargetPath ?? current.TargetPath,
                request.Arguments ?? current.Arguments,
                request.WorkingDirectory ?? current.WorkingDirectory,
                request.DisplayName ?? current.Description,
                request.IconPath ?? current.IconLocation,
                request.RunAsAdministrator);
        }

        return CreateSendToEntry(path);
    }

    private static SpecialMenuEntry CreateSendToEntry(string path)
    {
        ShortcutInfo? shortcut = null;
        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            try { shortcut = ShortcutFile.Read(path); }
            catch { }
        }

        var displayName = DesktopIniStore.GetLocalizedFileName(path);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = Path.GetFileNameWithoutExtension(path);
        }

        var attributes = File.GetAttributes(path);
        var (iconPath, iconIndex, iconFallback) = ResolveSendToIcon(path, shortcut);
        return new SpecialMenuEntry
        {
            Id = EncodeId(SpecialMenuKind.SendTo, path),
            Kind = SpecialMenuKind.SendTo,
            DisplayName = displayName,
            KeyName = Path.GetFileName(path),
            IsEnabled = (attributes & FileAttributes.Hidden) == 0,
            IconPath = iconPath,
            IconIndex = iconIndex,
            Path = path,
            TargetPath = iconFallback ?? shortcut?.TargetPath,
            Arguments = shortcut?.Arguments,
            WorkingDirectory = shortcut?.WorkingDirectory,
            Notes = shortcut?.RunAsAdministrator == true ? "Run as administrator" : null,
            Metadata = new Dictionary<string, string>
            {
                ["EntryType"] = Directory.Exists(path) ? "Directory" : Path.GetExtension(path).TrimStart('.')
            }
        };
    }

    private static IEnumerable<SpecialMenuEntry> GetDynamicSendToDriveItems()
    {
        var items = new List<SpecialMenuEntry>();
        foreach (var drive in DriveInfo.GetDrives().OrderBy(static drive => drive.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (drive.DriveType != DriveType.Removable || !drive.IsReady)
                {
                    continue;
                }

                var rootPath = drive.RootDirectory.FullName;
                var driveName = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var label = string.Empty;
                try
                {
                    label = drive.VolumeLabel;
                }
                catch
                {
                    // Some removable media can become unavailable between IsReady and metadata reads.
                }

                items.Add(new SpecialMenuEntry
                {
                    Id = DynamicSendToDriveIdPrefix + driveName.TrimEnd(':').ToUpperInvariant(),
                    Kind = SpecialMenuKind.SendTo,
                    DisplayName = string.IsNullOrWhiteSpace(label) ? driveName : $"{label} ({driveName})",
                    KeyName = driveName,
                    IsEnabled = true,
                    IconPath = rootPath,
                    Path = rootPath,
                    TargetPath = rootPath,
                    CanEdit = false,
                    CanDelete = false,
                    Metadata = new Dictionary<string, string>
                    {
                        ["EntryType"] = "DynamicDrive",
                        ["IsExplorerGenerated"] = bool.TrueString
                    }
                });
            }
            catch
            {
                // Ignore drives that disappear or reject metadata access during enumeration.
            }
        }

        return items;
    }

    private static (string? IconPath, int IconIndex, string? FallbackPath) ResolveSendToIcon(string path, ShortcutInfo? shortcut)
    {
        if (shortcut is not null)
        {
            var (shortcutIconPath, shortcutIconIndex) = ParseIconLocation(shortcut.IconLocation);
            if (string.IsNullOrWhiteSpace(shortcutIconPath))
            {
                shortcutIconPath = shortcut.TargetPath;
                shortcutIconIndex = 0;
            }

            var fallback = File.Exists(shortcut.TargetPath) || Directory.Exists(shortcut.TargetPath)
                ? shortcut.TargetPath
                : path;
            return (shortcutIconPath, shortcutIconIndex, fallback);
        }

        if (Directory.Exists(path))
        {
            return (path, 0, path);
        }

        var extension = Path.GetExtension(path);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            using var extensionKey = Registry.ClassesRoot.OpenSubKey(extension, writable: false);
            var progId = extensionKey?.GetValue(null)?.ToString();
            if (!string.IsNullOrWhiteSpace(progId))
            {
                using var defaultIconKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\DefaultIcon", writable: false);
                var (defaultIconPath, defaultIconIndex) = ParseIconLocation(defaultIconKey?.GetValue(null)?.ToString());
                if (!string.IsNullOrWhiteSpace(defaultIconPath))
                {
                    return (defaultIconPath, defaultIconIndex, path);
                }
            }

            return (extension, 0, path);
        }

        return (path, 0, path);
    }

    private static IReadOnlyList<SpecialMenuEntry> GetWinXItems(BackendUserContext context)
    {
        var winXPath = GetWinXPath(context);
        if (!Directory.Exists(winXPath))
        {
            return [];
        }

        var result = new List<SpecialMenuEntry>();
        foreach (var groupPath in Directory.EnumerateDirectories(winXPath).OrderByDescending(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            var groupName = Path.GetFileName(groupPath);
            if (IsReservedWinXGroupName(groupName))
            {
                continue;
            }
            result.Add(new SpecialMenuEntry
            {
                Id = EncodeId(SpecialMenuKind.WinX, groupPath),
                Kind = SpecialMenuKind.WinX,
                DisplayName = groupName,
                KeyName = groupName,
                IsEnabled = (File.GetAttributes(groupPath) & FileAttributes.Hidden) == 0,
                Path = groupPath,
                GroupName = groupName,
                CanEdit = false,
                CanMove = false,
                Metadata = new Dictionary<string, string> { ["EntryType"] = "Group" }
            });

            foreach (var lnkPath in Directory.EnumerateFiles(groupPath, "*.lnk").OrderByDescending(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                result.Add(CreateWinXEntryFromPath(lnkPath, groupName));
            }
        }

        return result;
    }

    private static SpecialMenuEntry CreateWinXGroup(WinXCreateGroupRequest request, BackendUserContext context)
    {
        var groupName = ValidateWinXGroupName(request.GroupName);
        var groupPath = GetUniqueDirectoryPath(Path.Combine(GetWinXPath(context), groupName));
        EnsurePathUnder(groupPath, GetWinXPath(context));
        Directory.CreateDirectory(groupPath);
        var iniPath = Path.Combine(groupPath, "desktop.ini");
        File.WriteAllText(iniPath, string.Empty, Encoding.Unicode);
        File.SetAttributes(groupPath, File.GetAttributes(groupPath) | FileAttributes.ReadOnly);
        File.SetAttributes(iniPath, File.GetAttributes(iniPath) | FileAttributes.Hidden | FileAttributes.System);
        return GetWinXItems(context).First(item => string.Equals(item.Path, groupPath, StringComparison.OrdinalIgnoreCase));
    }

    private SpecialMenuEntry CreateWinXEntry(WinXCreateEntryRequest request, BackendUserContext context)
    {
        var winXPath = GetWinXPath(context);
        var groupName = ValidateWinXGroupName(request.GroupName);
        if (string.IsNullOrWhiteSpace(request.TargetPath))
        {
            throw new InvalidOperationException("Win+X target path cannot be empty.");
        }

        var groupPath = Path.Combine(winXPath, groupName);
        EnsurePathUnder(groupPath, winXPath);
        _logger.LogFireAndForget(
            $"WinXCreateStart: Sid={context.Sid}, ProfilePath={context.ProfilePath}, LocalAppData={context.LocalAppDataPath}, " +
            $"WinXPath={winXPath}, GroupName={groupName}, TargetPath={request.TargetPath}.");
        if (!Directory.Exists(groupPath))
        {
            throw new InvalidOperationException($"Win+X group '{request.GroupName}' does not exist.");
        }

        var index = Directory.GetFiles(groupPath, "*.lnk").Length + 1;
        var nameSource = string.IsNullOrWhiteSpace(request.DisplayName)
            ? Path.GetFileNameWithoutExtension(request.TargetPath)
            : request.DisplayName;
        var fileName = $"{index:00} - {RemoveIllegalChars(nameSource)}.lnk";
        var path = GetUniqueFilePath(Path.Combine(groupPath, fileName));
        EnsurePathUnder(path, winXPath);
        if (IsSamePath(request.TargetPath, path))
        {
            throw new InvalidOperationException("Win+X target path cannot be the output shortcut path.");
        }

        _logger.LogFireAndForget($"WinXCreatePathChosen: GroupPath={groupPath}, LnkPath={path}.");
        var stage = "WinXShortcutWriteStart";
        try
        {
            _logger.LogFireAndForget($"{stage}: LnkPath={path}, TargetPath={request.TargetPath}.");
            if (request.TargetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(request.TargetPath))
            {
                File.Copy(request.TargetPath, path, overwrite: false);
                WinXShortcutFile.Update(path, description: request.DisplayName);
            }
            else
            {
                WinXShortcutFile.Write(
                    path,
                    request.TargetPath,
                    request.Arguments,
                    request.WorkingDirectory,
                    request.DisplayName,
                    request.IconPath,
                    request.RunAsAdministrator);
            }
            _logger.LogFireAndForget($"WinXShortcutWriteEnd: LnkPath={path}.");

            stage = "WinXDesktopIniWriteStart";
            _logger.LogFireAndForget($"{stage}: LnkPath={path}, DisplayName={request.DisplayName}.");
            DesktopIniStore.SetLocalizedFileName(path, request.DisplayName);
            _logger.LogFireAndForget($"WinXDesktopIniWriteEnd: LnkPath={path}.");

            ShortcutInfo? finalShortcut = null;
            try { finalShortcut = WinXShortcutFile.Read(path); }
            catch (Exception ex)
            {
                _logger.LogFireAndForget(
                    RuntimeLogLevel.Warning,
                    $"WinXCreateShortcutReadWarning: LnkPath={path}, Error={ex.Message}.");
            }

            var intendedTarget = finalShortcut?.TargetPath;
            if (string.IsNullOrWhiteSpace(intendedTarget)
                && !request.TargetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                intendedTarget = request.TargetPath;
            }
            var intendedArguments = finalShortcut?.Arguments ?? request.Arguments ?? string.Empty;

            stage = "WinXHashStart";
            _logger.LogFireAndForget(
                $"{stage}: LnkPath={path}, FallbackTarget={intendedTarget ?? "<null>"}, FallbackArguments={intendedArguments}.");
            var hashResult = WinXHasher.HashLnkWithResult(path, intendedTarget, intendedArguments);
            _logger.LogFireAndForget(
                $"WinXHashEnd: LnkPath={path}, Success={hashResult.Success}, Error={hashResult.Error ?? "<none>"}, " +
                $"VerificationWarning={hashResult.VerificationWarning ?? "<none>"}.");
            if (!hashResult.Success)
            {
                throw new InvalidOperationException(
                    $"Win+X shortcut was created but could not be hashed, so Windows will not show it. " +
                    $"Target={hashResult.TargetPath}, GeneralizedTarget={hashResult.GeneralizedTargetPath}, Arguments={hashResult.Arguments}, Error={hashResult.Error}");
            }

            var item = CreateWinXEntryFromPath(path, groupName);
            _logger.LogFireAndForget($"WinXCreateEnd: LnkPath={path}, Success=True.");
            return item;
        }
        catch (Exception ex)
        {
            var hresult = ex is COMException ? $", HResult=0x{ex.HResult:X8}" : string.Empty;
            _logger.LogFireAndForget(
                RuntimeLogLevel.Error,
                $"WinXCreateFailed: Stage={stage}, LnkPath={path}{hresult}, Exception={ex}.");
            DeleteFileSystemItem(path, winXPath);
            throw;
        }
    }

    private static SpecialMenuEntry UpdateWinXEntry(WinXUpdateEntryRequest request, BackendUserContext context)
    {
        var path = DecodeId(request.Id);
        var winXPath = GetWinXPath(context);
        EnsurePathUnder(path, winXPath);
        var hashAffectingFieldsChanged = request.TargetPath is not null || request.Arguments is not null;
        var groupName = Path.GetFileName(Path.GetDirectoryName(path));
        if (!string.IsNullOrWhiteSpace(request.GroupName) && !string.Equals(request.GroupName, groupName, StringComparison.OrdinalIgnoreCase))
        {
            var targetGroupName = ValidateWinXGroupName(request.GroupName);
            var destinationGroupPath = Path.Combine(winXPath, targetGroupName);
            EnsurePathUnder(destinationGroupPath, winXPath);
            if (!Directory.Exists(destinationGroupPath))
            {
                throw new InvalidOperationException($"Win+X group '{request.GroupName}' does not exist.");
            }

            path = GetUniqueFilePath(Path.Combine(destinationGroupPath, Path.GetFileName(path)));
            EnsurePathUnder(path, winXPath);
            File.Move(DecodeId(request.Id), path);
            groupName = targetGroupName;
        }

        WinXShortcutFile.Update(
            path,
            request.TargetPath,
            request.Arguments,
            request.WorkingDirectory,
            request.DisplayName,
            request.IconPath,
            request.RunAsAdministrator);

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            DesktopIniStore.SetLocalizedFileName(path, request.DisplayName);
        }

        if (hashAffectingFieldsChanged)
        {
            ShortcutInfo? finalShortcut = null;
            try { finalShortcut = WinXShortcutFile.Read(path); }
            catch (Exception ex)
            {
                WinXHasher.FileLoggerHost.Log(
                    RuntimeLogLevel.Warning,
                    $"WinXUpdateShortcutReadWarning: Path={path}, Error={ex.Message}.");
            }

            var finalTarget = finalShortcut?.TargetPath ?? request.TargetPath;
            var finalArguments = finalShortcut?.Arguments ?? request.Arguments ?? string.Empty;
            WinXHasher.FileLoggerHost.Log(
                $"WinXUpdateHashStart: Path={path}, FallbackTarget={finalTarget ?? "<null>"}, FallbackArguments={finalArguments}.");
            var hashResult = WinXHasher.HashLnkWithResult(path, finalTarget, finalArguments);
            if (!hashResult.Success)
            {
                WinXHasher.FileLoggerHost.Log(
                    RuntimeLogLevel.Warning,
                    $"WinXUpdateHashFailed: Path={path}, Target={finalTarget ?? "<null>"}, Arguments={finalArguments}, Error={hashResult.Error}.");
                throw new InvalidOperationException(
                    $"Win+X shortcut was updated but could not be hashed, so Windows will not show it. " +
                    $"Target={hashResult.TargetPath}, GeneralizedTarget={hashResult.GeneralizedTargetPath}, Arguments={hashResult.Arguments}, Error={hashResult.Error}");
            }
        }
        else
        {
            WinXHasher.FileLoggerHost.Log(
                $"WinXUpdateHashSkipped: Path={path}, Reason=TargetAndArgumentsUnchanged.");
        }

        return CreateWinXEntryFromPath(path, groupName ?? string.Empty);
    }

    private static SpecialMenuEntry MoveWinX(WinXMoveRequest request, BackendUserContext context)
    {
        var path = DecodeId(request.Id);
        var winXPath = GetWinXPath(context);
        EnsurePathUnder(path, winXPath);
        var groupPath = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Invalid Win+X item path.");
        EnsurePathUnder(groupPath, winXPath);
        var groupPaths = Directory.GetDirectories(winXPath)
            .Where(static item => !IsReservedWinXGroupName(Path.GetFileName(item)))
            .OrderByDescending(static item => Path.GetFileName(item), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var groupIndex = groupPaths.FindIndex(item => string.Equals(item, groupPath, StringComparison.OrdinalIgnoreCase));
        if (groupIndex < 0)
        {
            return CreateWinXEntryFromPath(path, Path.GetFileName(groupPath));
        }

        var displayPaths = GetWinXGroupDisplayPaths(groupPath);
        var index = displayPaths.FindIndex(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return CreateWinXEntryFromPath(path, Path.GetFileName(groupPath));
        }

        if (request.MoveUp)
        {
            if (index > 0)
            {
                (displayPaths[index - 1], displayPaths[index]) = (displayPaths[index], displayPaths[index - 1]);
                var movedPath = RewriteWinXGroupOrder(groupPath, displayPaths, path, winXPath);
                return CreateWinXEntryFromPath(movedPath, Path.GetFileName(groupPath));
            }

            if (groupIndex == 0)
            {
                return CreateWinXEntryFromPath(path, Path.GetFileName(groupPath));
            }

            var targetGroupPath = groupPaths[groupIndex - 1];
            var movedToPrevious = MoveWinXEntryToGroup(path, groupPath, targetGroupPath, insertAtTop: false, winXPath);
            return CreateWinXEntryFromPath(movedToPrevious, Path.GetFileName(targetGroupPath));
        }

        if (index < displayPaths.Count - 1)
        {
            (displayPaths[index], displayPaths[index + 1]) = (displayPaths[index + 1], displayPaths[index]);
            var movedPath = RewriteWinXGroupOrder(groupPath, displayPaths, path, winXPath);
            return CreateWinXEntryFromPath(movedPath, Path.GetFileName(groupPath));
        }

        if (groupIndex >= groupPaths.Count - 1)
        {
            return CreateWinXEntryFromPath(path, Path.GetFileName(groupPath));
        }

        var nextGroupPath = groupPaths[groupIndex + 1];
        var movedToNext = MoveWinXEntryToGroup(path, groupPath, nextGroupPath, insertAtTop: true, winXPath);
        return CreateWinXEntryFromPath(movedToNext, Path.GetFileName(nextGroupPath));
    }

    private static SpecialMenuEntry CreateWinXEntryFromPath(string path, string groupName)
    {
        ShortcutInfo? shortcut = null;
        try { shortcut = WinXShortcutFile.Read(path); }
        catch { }

        var localizedDisplayName = DesktopIniStore.GetLocalizedFileName(path, translate: true);
        var displayName = !string.IsNullOrWhiteSpace(localizedDisplayName)
            ? localizedDisplayName
            : ShellMetadataResolver.ResolveResourceString(shortcut?.Description);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = shortcut?.Description;
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = Path.GetFileNameWithoutExtension(path);
        }

        displayName = StripWinXOrderPrefix(displayName);
        var (iconPath, iconIndex) = ParseIconLocation(shortcut?.IconLocation);

        return new SpecialMenuEntry
        {
            Id = EncodeId(SpecialMenuKind.WinX, path),
            Kind = SpecialMenuKind.WinX,
            DisplayName = displayName,
            KeyName = Path.GetFileName(path),
            IsEnabled = (File.GetAttributes(path) & FileAttributes.Hidden) == 0,
            Path = path,
            GroupName = groupName,
            TargetPath = shortcut?.TargetPath,
            Arguments = shortcut?.Arguments,
            WorkingDirectory = shortcut?.WorkingDirectory,
            IconPath = iconPath,
            IconIndex = iconIndex,
            CanMove = true,
            Notes = shortcut?.RunAsAdministrator == true ? "Run as administrator" : null,
            Metadata = new Dictionary<string, string> { ["EntryType"] = "Entry" }
        };
    }

    private static List<string> GetWinXGroupDisplayPaths(string groupPath) =>
        Directory.GetFiles(groupPath, "*.lnk")
            .OrderByDescending(static item => Path.GetFileName(item), StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string MoveWinXEntryToGroup(string path, string sourceGroupPath, string targetGroupPath, bool insertAtTop, string winXPath)
    {
        EnsurePathUnder(path, winXPath);
        EnsurePathUnder(sourceGroupPath, winXPath);
        EnsurePathUnder(targetGroupPath, winXPath);
        var localizedName = TryGetRawLocalizedFileName(path);
        TryDeleteLocalizedFileName(path);
        var stagedPath = Path.Combine(targetGroupPath, $"{Guid.NewGuid():N}.lnk");
        File.Move(path, stagedPath);
        TrySetLocalizedFileName(stagedPath, localizedName);

        var sourceDisplay = GetWinXGroupDisplayPaths(sourceGroupPath)
            .Where(item => !string.Equals(item, path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        RewriteWinXGroupOrder(sourceGroupPath, sourceDisplay, null, winXPath);

        var targetDisplay = GetWinXGroupDisplayPaths(targetGroupPath)
            .Where(item => !string.Equals(item, stagedPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (insertAtTop)
        {
            targetDisplay.Insert(0, stagedPath);
        }
        else
        {
            targetDisplay.Add(stagedPath);
        }

        return RewriteWinXGroupOrder(targetGroupPath, targetDisplay, stagedPath, winXPath);
    }

    private static void DeleteWinXItem(string? path, string winXPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Win+X item path is missing.");
        }

        EnsurePathUnder(path, winXPath);
        if (IsSamePath(path, winXPath))
        {
            throw new UnauthorizedAccessException("Refusing to delete the Win+X root folder.");
        }

        var name = Path.GetFileName(path);
        if (IsReservedWinXGroupName(name))
        {
            throw new UnauthorizedAccessException($"Refusing to delete internal Win+X folder '{name}'.");
        }

        if (File.Exists(path))
        {
            if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Win+X direct delete only supports .lnk entries.");
            }

            DesktopIniStore.DeleteLocalizedFileName(path);
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return;
        }

        if (Directory.Exists(path))
        {
            ClearAttributesRecursive(path);
            Directory.Delete(path, recursive: true);
        }
    }

    private static string RewriteWinXGroupOrder(string groupPath, IReadOnlyList<string> displayPaths, string? trackedPath, string winXPath)
    {
        EnsurePathUnder(groupPath, winXPath);
        var trackedTemp = string.Empty;
        var tempItems = new List<(string TempPath, string Suffix, string LocalizedName)>();
        foreach (var path in displayPaths)
        {
            EnsurePathUnder(path, winXPath);
            if (!File.Exists(path))
            {
                continue;
            }

            var localizedName = TryGetRawLocalizedFileName(path);
            TryDeleteLocalizedFileName(path);
            var tempPath = Path.Combine(groupPath, $"{Guid.NewGuid():N}.lnk");
            File.Move(path, tempPath);
            if (string.Equals(path, trackedPath, StringComparison.OrdinalIgnoreCase))
            {
                trackedTemp = tempPath;
            }

            tempItems.Add((tempPath, GetWinXFileNameSuffix(path), localizedName));
        }

        var trackedResult = trackedTemp;
        for (var index = 0; index < tempItems.Count; index++)
        {
            var item = tempItems[index];
            var order = tempItems.Count - index;
            var targetPath = GetUniqueFilePath(Path.Combine(groupPath, $"{order:00} - {item.Suffix}"));
            File.Move(item.TempPath, targetPath);
            TrySetLocalizedFileName(targetPath, item.LocalizedName);
            if (string.Equals(item.TempPath, trackedTemp, StringComparison.OrdinalIgnoreCase))
            {
                trackedResult = targetPath;
            }
        }

        return string.IsNullOrWhiteSpace(trackedResult) ? trackedPath ?? string.Empty : trackedResult;
    }

    private static string TryGetRawLocalizedFileName(string path)
    {
        try
        {
            return DesktopIniStore.GetLocalizedFileName(path, translate: false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TrySetLocalizedFileName(string path, string localizedName)
    {
        if (string.IsNullOrWhiteSpace(localizedName))
        {
            return;
        }

        try
        {
            DesktopIniStore.SetLocalizedFileName(path, localizedName);
        }
        catch
        {
        }
    }

    private static void TryDeleteLocalizedFileName(string path)
    {
        try
        {
            DesktopIniStore.DeleteLocalizedFileName(path);
        }
        catch
        {
        }
    }

    private static string GetWinXFileNameSuffix(string path)
    {
        var fileName = Path.GetFileName(path);
        var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
        return RemoveIllegalChars(StripWinXOrderPrefix(withoutExtension)) + ".lnk";
    }

    private static string ValidateWinXGroupName(string value)
    {
        var groupName = ValidatePlainDirectoryName(value, "Win+X group name");
        if (IsReservedWinXGroupName(groupName))
        {
            throw new InvalidOperationException($"'{groupName}' is a reserved internal folder name and cannot be used as a Win+X group.");
        }

        return groupName;
    }

    private static bool IsReservedWinXGroupName(string? groupName) =>
        string.Equals(groupName, DeletedFolderName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(groupName, BackupFolderName, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<SpecialMenuEntry> GetDragDropItems()
    {
        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Folder"] = @"Folder\shellex",
            ["Directory"] = @"Directory\shellex",
            ["Drive"] = @"Drive\shellex",
            ["AllFilesystemObjects"] = @"AllFilesystemObjects\shellex"
        };
        var result = new List<SpecialMenuEntry>();
        foreach (var group in groups)
        {
            foreach (var part in new[] { "DragDropHandlers", "-DragDropHandlers" })
            {
                using var handlers = Registry.ClassesRoot.OpenSubKey($@"{group.Value}\{part}", writable: false);
                if (handlers is null)
                {
                    continue;
                }

                foreach (var keyName in handlers.GetSubKeyNames())
                {
                    using var key = handlers.OpenSubKey(keyName);
                    var guidText = key?.GetValue(null)?.ToString();
                    if (!Guid.TryParse(guidText, out var guid) && !Guid.TryParse(keyName, out guid))
                    {
                        continue;
                    }

                    var displayName = GuidMetadataCatalog.GetDisplayName(guid) ?? keyName;
                    var icon = GuidMetadataCatalog.GetIconLocation(guid);
                    var registryPath = $@"HKEY_CLASSES_ROOT\{group.Value}\{part}\{keyName}";
                    result.Add(new SpecialMenuEntry
                    {
                        Id = EncodeId(SpecialMenuKind.DragDrop, registryPath),
                        Kind = SpecialMenuKind.DragDrop,
                        DisplayName = displayName,
                        KeyName = keyName,
                        IsEnabled = string.Equals(part, "DragDropHandlers", StringComparison.OrdinalIgnoreCase),
                        IconPath = icon.IconPath,
                        IconIndex = icon.IconIndex,
                        RegistryPath = registryPath,
                        GroupName = group.Key,
                        TargetPath = GuidMetadataCatalog.GetFilePath(guid),
                        Metadata = new Dictionary<string, string> { ["Guid"] = guid.ToString("B") }
                    });
                }
            }
        }

        result.Insert(0, new SpecialMenuEntry
        {
            Id = "dragdrop:default-drop-effect",
            Kind = SpecialMenuKind.DragDrop,
            DisplayName = "DefaultDropEffect",
            KeyName = "DefaultDropEffect",
            IsEnabled = true,
            CanDelete = false,
            CanEdit = true,
            Notes = GetDefaultDropEffect().ToString(),
            Metadata = new Dictionary<string, string> { ["EntryType"] = "DefaultDropEffect" }
        });
        return result;
    }

    private static SpecialMenuEntry CreateDragDrop(DragDropCreateRequest request)
    {
        if (!Guid.TryParse(request.GuidText, out var guid))
        {
            throw new InvalidOperationException("The GUID format is invalid.");
        }

        var shellExPath = request.GroupName switch
        {
            "Folder" => @"Folder\shellex",
            "Directory" => @"Directory\shellex",
            "Drive" => @"Drive\shellex",
            "AllFilesystemObjects" => @"AllFilesystemObjects\shellex",
            _ => throw new InvalidOperationException("Unknown drag-drop group.")
        };
        var path = $@"{shellExPath}\DragDropHandlers\{guid:B}";
        Registry.SetValue($@"HKEY_CLASSES_ROOT\{path}", string.Empty, guid.ToString("B"), RegistryValueKind.String);
        return GetDragDropItems().First(item => string.Equals(item.RegistryPath, $@"HKEY_CLASSES_ROOT\{path}", StringComparison.OrdinalIgnoreCase));
    }

    private static SpecialMenuEntry UpdateDefaultDropEffect(DefaultDropEffect effect)
    {
        var value = (int)effect;
        foreach (var path in new[] { "*", "AllFilesystemObjects", "Folder", "Directory" })
        {
            Registry.SetValue($@"HKEY_CLASSES_ROOT\{path}", "DefaultDropEffect", value, RegistryValueKind.DWord);
        }

        return GetDragDropItems().First(static item => item.Id == "dragdrop:default-drop-effect");
    }

    private static IReadOnlyList<SpecialMenuEntry> GetOpenWithItems(BackendUserContext context)
    {
        var result = new List<SpecialMenuEntry>
        {
            GetUseStoreOpenWithItem(context)
        };

        PopulateOpenWithItems(
            Registry.Users,
            $@"{context.Sid}\{UserClassesPath}\{ApplicationsPath}",
            $@"HKEY_USERS\{context.Sid}\{UserClassesPath}\{ApplicationsPath}",
            "User",
            result);

        PopulateOpenWithItems(
            Registry.LocalMachine,
            $@"{MachineClassesPath}\{ApplicationsPath}",
            $@"HKEY_LOCAL_MACHINE\{MachineClassesPath}\{ApplicationsPath}",
            "Machine",
            result);

        return result
            .GroupBy(static item => item.RegistryPath ?? item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SpecialMenuEntry GetUseStoreOpenWithItem(BackendUserContext context)
    {
        var machineDisabled = Registry.LocalMachine
            .OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Explorer", writable: false)
            ?.GetValue("NoUseStoreOpenWith") is int machineValue && machineValue == 1;
        var userDisabled = Registry.Users
            .OpenSubKey($@"{context.Sid}\{OpenWithPolicyPath}", writable: false)
            ?.GetValue("NoUseStoreOpenWith") is int userValue && userValue == 1;

        return new SpecialMenuEntry
        {
            Id = "openwith:use-store",
            Kind = SpecialMenuKind.OpenWith,
            DisplayName = "Look for an app in the Microsoft Store",
            KeyName = "NoUseStoreOpenWith",
            IsEnabled = !machineDisabled && !userDisabled,
            IconPath = "imageres.dll",
            IconIndex = -102,
            RegistryPath = $@"HKEY_USERS\{context.Sid}\{OpenWithPolicyPath}",
            CanDelete = false,
            Metadata = new Dictionary<string, string>
            {
                ["EntryType"] = "Policy",
                ["CanEditDetails"] = "false",
                ["RegistryScope"] = "UserPolicy"
            }
        };
    }

    private static void PopulateOpenWithItems(
        RegistryKey root,
        string subPath,
        string displayRootPath,
        string scope,
        List<SpecialMenuEntry> result)
    {
        using var applicationsKey = root.OpenSubKey(subPath, writable: false);
        if (applicationsKey is null)
        {
            return;
        }

        foreach (var appName in applicationsKey.GetSubKeyNames().OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (!appName.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            var originalAppName = StripDeletedSuffixes(appName, out var deletedSuffixCount);
            using var appKey = applicationsKey.OpenSubKey(appName, writable: false);
            using var shellKey = appKey?.OpenSubKey("shell", writable: false);
            if (appKey is null || shellKey is null)
            {
                continue;
            }

            var verbName = ChooseOpenWithVerb(shellKey);
            if (verbName is null)
            {
                continue;
            }

            using var verbKey = shellKey.OpenSubKey(verbName, writable: false);
            using var commandKey = verbKey?.OpenSubKey("command", writable: false);
            var command = commandKey?.GetValue(null)?.ToString();
            var targetPath = ExtractCommandExecutablePath(command);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                continue;
            }

            // A soft-deleted Open With registration is deliberately retained under
            // <application>.deleted. It must be projected as deleted, not accepted
            // as a normal app name and soft-deleted again on the next UI action.
            // Strip all historical suffixes so older repeated keys remain recoverable.
            var appPath = $@"{displayRootPath}\{originalAppName}";
            var deletedAppPath = $@"{displayRootPath}\{appName}";
            var verbPath = $@"{appPath}\shell\{verbName}";
            var commandPath = $@"{verbPath}\command";
            var displayName = ResolveOpenWithDisplayName(appKey, appName, targetPath);
            var icon = ShellMetadataResolver.ResolveVerbIcon(verbKey!, command);

            result.Add(new SpecialMenuEntry
            {
                Id = EncodeId(SpecialMenuKind.OpenWith, commandPath),
                Kind = SpecialMenuKind.OpenWith,
                DisplayName = displayName,
                KeyName = appName,
                IsEnabled = appKey.GetValue("NoOpenWith") is null,
                IconPath = icon.IconPath ?? targetPath,
                IconIndex = icon.IconIndex,
                RegistryPath = commandPath,
                CommandText = command,
                TargetPath = targetPath,
                Metadata = new Dictionary<string, string>
                {
                    ["AppRegistryPath"] = appPath,
                    ["VerbRegistryPath"] = verbPath,
                    ["VerbName"] = verbName,
                    ["RegistryScope"] = scope,
                    ["IsDeleted"] = (deletedSuffixCount > 0).ToString(),
                    ["DeletedPath"] = deletedSuffixCount > 0 ? deletedAppPath : string.Empty,
                    ["DeletedAt"] = deletedSuffixCount > 0 ? "Unknown (legacy soft delete)" : string.Empty
                }
            });
        }
    }

    private static string StripDeletedSuffixes(string appName, out int count)
    {
        count = 0;
        var result = appName;
        while (result.EndsWith(DeletedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            result = result[..^DeletedSuffix.Length];
            count++;
        }

        return result;
    }

    private static bool IsDeletedRegistryPathFor(string originalPath, string deletedPath)
    {
        var candidate = deletedPath;
        while (candidate.EndsWith(DeletedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^DeletedSuffix.Length];
        }

        return string.Equals(candidate, originalPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ChooseOpenWithVerb(RegistryKey shellKey)
    {
        var names = shellKey.GetSubKeyNames().ToList();
        if (names.Count == 0)
        {
            return null;
        }

        var openIndex = names.FindIndex(static name => name.Equals("open", StringComparison.OrdinalIgnoreCase));
        if (openIndex > 0)
        {
            var open = names[openIndex];
            names.RemoveAt(openIndex);
            names.Insert(0, open);
        }

        foreach (var name in names)
        {
            using var verbKey = shellKey.OpenSubKey(name, writable: false);
            using var commandKey = verbKey?.OpenSubKey("command", writable: false);
            if (commandKey?.GetValue(null) is not null && verbKey?.GetValue("NeverDefault") is null)
            {
                return name;
            }
        }

        return null;
    }

    private static string ResolveOpenWithDisplayName(RegistryKey appKey, string appName, string targetPath)
    {
        var targetFileName = Path.GetFileName(targetPath);
        if (appName.Equals(targetFileName, StringComparison.OrdinalIgnoreCase))
        {
            var friendlyName = ShellMetadataResolver.ResolveResourceString(appKey.GetValue("FriendlyAppName")?.ToString());
            if (!string.IsNullOrWhiteSpace(friendlyName))
            {
                return friendlyName;
            }
        }

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(targetPath);
            if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription))
            {
                return versionInfo.FileDescription;
            }
        }
        catch
        {
        }

        return string.IsNullOrWhiteSpace(targetFileName) ? appName : targetFileName;
    }

    private static SpecialMenuEntry CreateOpenWith(OpenWithCreateRequest request, BackendUserContext context)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.TargetPath))
        {
            throw new InvalidOperationException("Open With items require a display name and target path.");
        }

        var targetPath = Environment.ExpandEnvironmentVariables(request.TargetPath.Trim().Trim('"'));
        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("The selected application was not found.", targetPath);
        }

        var appName = Path.GetFileName(targetPath);
        if (string.IsNullOrWhiteSpace(appName) || !appName.Contains('.', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Open With application keys require a filename with an extension.");
        }

        var appPath = $@"HKEY_USERS\{context.Sid}\{UserClassesPath}\{ApplicationsPath}\{SanitizeKeyName(appName)}";
        var commandPath = $@"{appPath}\shell\open\command";
        var command = BuildOpenWithCommand(targetPath, request.Arguments);
        using (var appKey = CreateRegistryKey(appPath, context)
            ?? throw new InvalidOperationException($"Unable to create {appPath}."))
        {
            appKey.SetValue("FriendlyAppName", request.DisplayName, RegistryValueKind.String);
            appKey.DeleteValue("NoOpenWith", throwOnMissingValue: false);
        }

        using (var commandKey = CreateRegistryKey(commandPath, context)
            ?? throw new InvalidOperationException($"Unable to create {commandPath}."))
        {
            commandKey.SetValue(string.Empty, command, RegistryValueKind.String);
        }

        return GetOpenWithItems(context).First(item => string.Equals(item.RegistryPath, commandPath, StringComparison.OrdinalIgnoreCase));
    }

    private static SpecialMenuEntry UpdateOpenWith(OpenWithUpdateRequest request, BackendUserContext context)
    {
        var commandPath = DecodeId(request.Id);
        var appPath = GetOpenWithAppPath(commandPath);
        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            using var appKey = OpenRegistryKey(appPath, writable: true, context)
                ?? throw new InvalidOperationException($"Unable to open {appPath}.");
            appKey.SetValue("FriendlyAppName", request.DisplayName, RegistryValueKind.String);
        }

        if (!string.IsNullOrWhiteSpace(request.Command))
        {
            var existing = OpenRegistryKey(commandPath, writable: false, context)?.GetValue(null)?.ToString();
            var existingTarget = ExtractCommandExecutablePath(existing);
            var newTarget = ExtractCommandExecutablePath(request.Command);
            if (!string.IsNullOrWhiteSpace(existingTarget)
                && !string.IsNullOrWhiteSpace(newTarget)
                && !existingTarget.Equals(newTarget, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Changing the target application path is not supported for existing Open With items.");
            }

            using var commandKey = OpenRegistryKey(commandPath, writable: true, context)
                ?? throw new InvalidOperationException($"Unable to open {commandPath}.");
            commandKey.SetValue(string.Empty, request.Command, RegistryValueKind.String);
        }

        return GetOpenWithItems(context).First(item => string.Equals(item.RegistryPath, commandPath, StringComparison.OrdinalIgnoreCase));
    }

    private static SpecialMenuEntry SetOpenWithEnabled(SpecialMenuEntry item, bool enabled, BackendUserContext context)
    {
        if (item.Id.Equals("openwith:use-store", StringComparison.OrdinalIgnoreCase))
        {
            SetUseStoreOpenWithEnabled(enabled, context);
            return GetUseStoreOpenWithItem(context);
        }

        var commandPath = item.RegistryPath ?? DecodeId(item.Id);
        var appPath = item.Metadata.TryGetValue("AppRegistryPath", out var metadataAppPath)
            ? metadataAppPath
            : GetOpenWithAppPath(commandPath);
        using var appKey = OpenRegistryKey(appPath, writable: true, context)
            ?? throw new InvalidOperationException($"Unable to open {appPath}.");
        if (enabled)
        {
            appKey.DeleteValue("NoOpenWith", throwOnMissingValue: false);
        }
        else
        {
            appKey.SetValue("NoOpenWith", string.Empty, RegistryValueKind.String);
        }

        return item with { IsEnabled = enabled };
    }

    private static void SetUseStoreOpenWithEnabled(bool enabled, BackendUserContext context)
    {
        using (var machineKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Explorer", writable: true))
        {
            if (enabled)
            {
                machineKey?.DeleteValue("NoUseStoreOpenWith", throwOnMissingValue: false);
            }
            else
            {
                machineKey?.SetValue("NoUseStoreOpenWith", 1, RegistryValueKind.DWord);
            }
        }

        using var userKey = Registry.Users.CreateSubKey($@"{context.Sid}\{OpenWithPolicyPath}", writable: true);
        if (enabled)
        {
            userKey?.DeleteValue("NoUseStoreOpenWith", throwOnMissingValue: false);
        }
        else
        {
            userKey?.SetValue("NoUseStoreOpenWith", 1, RegistryValueKind.DWord);
        }
    }

    private static string GetOpenWithAppPath(string commandPath)
    {
        var normalized = commandPath.TrimEnd('\\');
        const string suffix = @"\shell\";
        var shellIndex = normalized.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
        if (shellIndex < 0)
        {
            throw new InvalidOperationException("The Open With registry path is invalid.");
        }

        return normalized[..shellIndex];
    }

    private static string GetOpenWithItemAppPath(SpecialMenuEntry item)
        => item.Metadata.TryGetValue("AppRegistryPath", out var appPath) && !string.IsNullOrWhiteSpace(appPath)
            ? appPath
            : GetOpenWithAppPath(item.RegistryPath ?? DecodeId(item.Id));

    private static string BuildOpenWithCommand(string targetPath, string? arguments)
    {
        var args = string.IsNullOrWhiteSpace(arguments) ? "\"%1\"" : arguments.Trim();
        return $"\"{targetPath}\" {args}".TrimEnd();
    }

    private static string? ExtractCommandExecutablePath(string? commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(commandText.Trim());
        if (expanded.StartsWith('"'))
        {
            var closingQuoteIndex = expanded.IndexOf('"', 1);
            if (closingQuoteIndex > 1)
            {
                return expanded[1..closingQuoteIndex];
            }
        }

        var firstToken = expanded.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstToken) ? null : firstToken.Trim('"');
    }

    private static IReadOnlyList<SpecialMenuEntry> GetCommandStoreItems()
    {
        using var key = Registry.LocalMachine.OpenSubKey(CommandStorePath, writable: false);
        if (key is null)
        {
            return [];
        }

        return key.GetSubKeyNames()
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                using var itemKey = key.OpenSubKey(name);
                var displayName = itemKey is null ? name : ShellMetadataResolver.ResolveVerbDisplayName(itemKey, name);
                var command = itemKey?.OpenSubKey("command")?.GetValue(null)?.ToString();
                var icon = itemKey is null ? (null, 0) : ShellMetadataResolver.ResolveVerbIcon(itemKey, command);
                return new SpecialMenuEntry
                {
                    Id = EncodeId(SpecialMenuKind.CommandStore, $@"HKEY_LOCAL_MACHINE\{CommandStorePath}\{name}"),
                    Kind = SpecialMenuKind.CommandStore,
                    DisplayName = displayName,
                    KeyName = name,
                    IsEnabled = itemKey is null || ShellVerbVisibility.IsEnabled(itemKey),
                    IconPath = icon.Item1,
                    IconIndex = icon.Item2,
                    RegistryPath = $@"HKEY_LOCAL_MACHINE\{CommandStorePath}\{name}",
                    CommandText = command
                };
            })
            .ToArray();
    }

    private static SpecialMenuEntry CreateCommandStore(SpecialMenuEntry request)
    {
        if (string.IsNullOrWhiteSpace(request.KeyName) || string.IsNullOrWhiteSpace(request.CommandText))
        {
            throw new InvalidOperationException("CommandStore items require a key name and command.");
        }

        var path = $@"HKEY_LOCAL_MACHINE\{CommandStorePath}\{SanitizeKeyName(request.KeyName)}";
        Registry.SetValue(path, "MUIVerb", request.DisplayName, RegistryValueKind.String);
        if (!string.IsNullOrWhiteSpace(request.IconPath))
        {
            Registry.SetValue(path, "Icon", request.IconPath, RegistryValueKind.String);
        }

        Registry.SetValue($@"{path}\command", string.Empty, request.CommandText, RegistryValueKind.String);
        return GetCommandStoreItems().First(item => string.Equals(item.RegistryPath, path, StringComparison.OrdinalIgnoreCase));
    }

    private static SpecialMenuEntry UpdateCommandStore(SpecialMenuEntry request)
    {
        var path = DecodeId(request.Id);
        Registry.SetValue(path, "MUIVerb", request.DisplayName, RegistryValueKind.String);
        SetOptionalRegistryValue(path, "Icon", request.IconPath);
        if (!string.IsNullOrWhiteSpace(request.CommandText))
        {
            Registry.SetValue($@"{path}\command", string.Empty, request.CommandText, RegistryValueKind.String);
        }

        return GetCommandStoreItems().First(item => string.Equals(item.RegistryPath, path, StringComparison.OrdinalIgnoreCase));
    }

    private static SpecialMenuEntry SetCommandStoreEnabled(SpecialMenuEntry item, bool enabled)
    {
        using var key = OpenRegistryKey(item.RegistryPath ?? DecodeId(item.Id), writable: true)
            ?? throw new InvalidOperationException("Unable to open CommandStore item.");
        ShellVerbVisibility.SetEnabled(key, item.RegistryPath ?? DecodeId(item.Id), enabled);

        return item with { IsEnabled = ShellVerbVisibility.IsEnabled(key) };
    }

    private static IReadOnlyList<SpecialMenuEntry> GetGuidBlockItems()
    {
        using var key = Registry.LocalMachine.OpenSubKey(GuidBlockedPath, writable: false);
        if (key is null)
        {
            return [];
        }

        return key.GetValueNames()
            .Where(static name => Guid.TryParse(name, out _))
            .Select(name =>
            {
                var guid = Guid.Parse(name);
                var icon = GuidMetadataCatalog.GetIconLocation(guid);
                return new SpecialMenuEntry
                {
                    Id = EncodeId(SpecialMenuKind.GuidBlock, name),
                    Kind = SpecialMenuKind.GuidBlock,
                    DisplayName = GuidMetadataCatalog.GetDisplayName(guid) ?? key.GetValue(name)?.ToString() ?? name,
                    KeyName = name,
                    IsEnabled = true,
                    IconPath = icon.IconPath,
                    IconIndex = icon.IconIndex,
                    RegistryPath = $@"HKEY_LOCAL_MACHINE\{GuidBlockedPath}",
                    TargetPath = GuidMetadataCatalog.GetFilePath(guid),
                    Metadata = new Dictionary<string, string> { ["Guid"] = name }
                };
            })
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SpecialMenuEntry CreateGuidBlock(GuidBlockCreateRequest request)
    {
        if (!Guid.TryParse(request.GuidText, out var guid))
        {
            throw new InvalidOperationException("The GUID format is invalid.");
        }

        using var key = Registry.LocalMachine.CreateSubKey(GuidBlockedPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open Blocked shell extensions key.");
        key.SetValue(guid.ToString("B"), request.DisplayName ?? string.Empty, RegistryValueKind.String);
        return GetGuidBlockItems().First(item => string.Equals(item.KeyName, guid.ToString("B"), StringComparison.OrdinalIgnoreCase));
    }

    private static SpecialMenuEntry SetGuidBlockEnabled(SpecialMenuEntry item, bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.LocalMachine.CreateSubKey(GuidBlockedPath, writable: true)
                ?? throw new InvalidOperationException("Unable to open Blocked shell extensions key.");
            key.SetValue(item.KeyName, item.DisplayName ?? string.Empty, RegistryValueKind.String);
        }
        else
        {
            DeleteRegistryValue(Registry.LocalMachine, GuidBlockedPath, item.KeyName);
        }

        return item with { IsEnabled = enabled };
    }

    private static IReadOnlyList<SpecialMenuEntry> GetIeItems()
    {
        var result = new List<SpecialMenuEntry>();

        using var root = Registry.CurrentUser.OpenSubKey(IeRootPath, writable: false);

        if (root is not null)
        {
            PopulateIeItemsFromRoot(root, result);
        }

        return result.OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void PopulateIeItemsFromRoot(RegistryKey root, List<SpecialMenuEntry> result)
    {
        foreach (var part in new[] { "MenuExt", "-MenuExt" })
        {
            using var menuKey = root.OpenSubKey(part, writable: false);
            if (menuKey is null)
            {
                continue;
            }

            foreach (var keyName in menuKey.GetSubKeyNames())
            {
                using var itemKey = menuKey.OpenSubKey(keyName);
                var command = itemKey?.GetValue(null)?.ToString();
                if (string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                result.Add(new SpecialMenuEntry
                {
                    Id = EncodeId(SpecialMenuKind.InternetExplorer, $@"HKEY_CURRENT_USER\{IeRootPath}\{part}\{keyName}"),
                    Kind = SpecialMenuKind.InternetExplorer,
                    DisplayName = keyName,
                    KeyName = keyName,
                    IsEnabled = string.Equals(part, "MenuExt", StringComparison.OrdinalIgnoreCase),
                    RegistryPath = $@"HKEY_CURRENT_USER\{IeRootPath}\{part}\{keyName}",
                    CommandText = command,
                    TargetPath = ShellMetadataResolver.ResolveResourceString(command)
                });
            }
        }
    }

    private static SpecialMenuEntry CreateIe(IeMenuCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.Command))
        {
            throw new InvalidOperationException("IE menu items require a name and command.");
        }

        var path = $@"HKEY_CURRENT_USER\{IeRootPath}\MenuExt\{request.DisplayName.Replace("\\", string.Empty, StringComparison.Ordinal)}";
        using var key = CreateRegistryKey(path)
            ?? throw new InvalidOperationException($"Unable to create {path}.");
        key.SetValue(string.Empty, request.Command, RegistryValueKind.String);
        return GetIeItems().First(item => string.Equals(item.RegistryPath, path, StringComparison.OrdinalIgnoreCase));
    }

    private static SpecialMenuEntry UpdateIe(IeMenuUpdateRequest request)
    {
        var path = DecodeId(request.Id);
        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            var parent = path[..path.LastIndexOf('\\')];
            var newPath = $@"{parent}\{request.DisplayName.Replace("\\", string.Empty, StringComparison.Ordinal)}";
            MoveRegistryKey(path, newPath);
            path = newPath;
        }

        if (!string.IsNullOrWhiteSpace(request.Command))
        {
            using var key = OpenRegistryKey(path, writable: true)
                ?? throw new InvalidOperationException($"Unable to open {path}.");
            key.SetValue(string.Empty, request.Command, RegistryValueKind.String);
        }

        return GetIeItems().First(item => string.Equals(item.RegistryPath, path, StringComparison.OrdinalIgnoreCase));
    }

    private static SpecialMenuEntry SetDragDropEnabled(SpecialMenuEntry item, bool enabled)
    {
        try
        {
            EnableSecurityPrivilege();
            EnableBackupPrivilege();

            return SetRenameBackedRegistryItemEnabled(item, enabled, "DragDropHandlers", "-DragDropHandlers");
        }
        catch (UnauthorizedAccessException)
        {
            try
            {
                var source = item.RegistryPath ?? DecodeId(item.Id);
                var target = source.Replace(
                    enabled ? @"\-DragDropHandlers\" : @"\DragDropHandlers\",
                    enabled ? @"\DragDropHandlers\" : @"\-DragDropHandlers\",
                    StringComparison.OrdinalIgnoreCase);

                MoveRegistryKeyWithElevation(source, target);
                return item with
                {
                    Id = EncodeId(item.Kind, target),
                    IsEnabled = enabled,
                    RegistryPath = target
                };
            }
            catch
            {
                throw;
            }
        }
    }

    private static void MoveRegistryKeyWithElevation(string sourcePath, string targetPath)
    {
        try
        {
            MoveRegistryKeyCore(sourcePath, targetPath, context: null);
        }
        catch (UnauthorizedAccessException)
        {
            EnableSecurityPrivilege();

            using var sourceKey = OpenRegistryKeyWithFallback(sourcePath, writable: false);
            if (sourceKey is null)
            {
                throw new InvalidOperationException($"Unable to open source registry key: {sourcePath}");
            }

            CreateRegistryKeyCopyWithFallback(sourceKey, targetPath);
            DeleteRegistryTreeWithFallback(sourcePath);
        }
    }

    private static RegistryKey? OpenRegistryKeyWithFallback(string? fullPath, bool writable)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        var (root, subPath) = SplitRegistryPath(fullPath, context: null);

        try
        {
            return root.OpenSubKey(subPath, writable);
        }
        catch (UnauthorizedAccessException)
        {
            if (root == Registry.ClassesRoot)
            {
                try
                {
                    var machineRoot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes", writable: false);
                    return machineRoot?.OpenSubKey(subPath, writable);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }
    }

    private static void CreateRegistryKeyCopyWithFallback(RegistryKey source, string targetPath)
    {
        var (root, subPath) = SplitRegistryPath(targetPath, context: null);

        RegistryKey? target = null;
        try
        {
            target = root.CreateSubKey(subPath, writable: true);
        }
        catch (UnauthorizedAccessException)
        {
            if (root == Registry.ClassesRoot)
            {
                var machineRoot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes", writable: true);
                if (machineRoot is not null)
                {
                    target = machineRoot.CreateSubKey(subPath, writable: true);
                }
            }
        }

        if (target is null)
        {
            throw new InvalidOperationException($"Unable to create target registry key: {targetPath}");
        }

        using (target)
        {
            foreach (var valueName in source.GetValueNames())
            {
                target.SetValue(valueName, source.GetValue(valueName)!, source.GetValueKind(valueName));
            }

            foreach (var subKeyName in source.GetSubKeyNames())
            {
                using var subKey = source.OpenSubKey(subKeyName, writable: false);
                if (subKey is not null)
                {
                    CreateRegistryKeyCopyWithFallback(subKey, $@"{targetPath}\{subKeyName}");
                }
            }
        }
    }

    private static void DeleteRegistryTreeWithFallback(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        try
        {
            DeleteRegistryTree(fullPath, context: null);
        }
        catch (UnauthorizedAccessException)
        {
            var (root, subPath) = SplitRegistryPath(fullPath, context: null);
            var parentPath = subPath.Contains('\\') ? subPath[..subPath.LastIndexOf('\\')] : string.Empty;
            var keyName = subPath.Contains('\\') ? subPath[(subPath.LastIndexOf('\\') + 1)..] : subPath;

            if (root == Registry.ClassesRoot)
            {
                var machineRoot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes", writable: true);
                if (machineRoot is not null)
                {
                    using var parent = machineRoot.OpenSubKey(parentPath, writable: true);
                    parent?.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
                }
            }
        }
    }

    private static bool EnableBackupPrivilege()
    {
        try
        {
            IntPtr tokenHandle;
            var processHandle = Process.GetCurrentProcess().SafeHandle;
            if (!OpenProcessToken(processHandle.DangerousGetHandle(), TOKEN_ADJUST_PRIVILEGES | TOKEN_READ, out tokenHandle))
            {
                return false;
            }

            try
            {
                var privilege = new LUID_AND_ATTRIBUTES
                {
                    Luid = new LUID(),
                    Attributes = SE_PRIVILEGE_ENABLED
                };

                if (!LookupPrivilegeValue(null, SE_BACKUP_NAME, out privilege.Luid))
                {
                    return false;
                }

                var privileges = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = [privilege]
                };

                var length = 0u;
                if (!AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0u, IntPtr.Zero, ref length))
                {
                    return false;
                }

                return Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        catch
        {
            return false;
        }
    }

    private const string SE_BACKUP_NAME = "SeBackupPrivilege";

    private static SpecialMenuEntry SetRenameBackedRegistryItemEnabled(SpecialMenuEntry item, bool enabled, string enabledPart, string disabledPart)
    {
        EnableSecurityPrivilege();
        return SetRenameBackedRegistryItemEnabled(item, enabled, enabledPart, disabledPart, context: null);
    }

    private static SpecialMenuEntry SetRenameBackedRegistryItemEnabled(SpecialMenuEntry item, bool enabled, string enabledPart, string disabledPart, BackendUserContext? context)
    {
        var source = item.RegistryPath ?? DecodeId(item.Id);
        var target = source.Replace(enabled ? $@"\{disabledPart}\" : $@"\{enabledPart}\", enabled ? $@"\{enabledPart}\" : $@"\{disabledPart}\", StringComparison.OrdinalIgnoreCase);
        MoveRegistryKey(source, target, context);
        return item with
        {
            Id = EncodeId(item.Kind, target),
            IsEnabled = enabled,
            RegistryPath = target
        };
    }

    private static SpecialMenuEntry SetFileSystemItemEnabled(SpecialMenuEntry item, bool enabled, string allowedRoot)
    {
        if (IsDynamicSendToDrive(item))
        {
            throw new InvalidOperationException("Explorer-generated SendTo drive targets cannot be toggled.");
        }

        var path = item.Path ?? DecodeId(item.Id);
        EnsurePathUnder(path, allowedRoot);
        var attributes = File.GetAttributes(path);
        attributes = enabled ? attributes & ~FileAttributes.Hidden : attributes | FileAttributes.Hidden;
        File.SetAttributes(path, attributes);
        return item with { IsEnabled = enabled };
    }

    private static DefaultDropEffect GetDefaultDropEffect()
    {
        foreach (var path in new[] { "*", "AllFilesystemObjects", "Folder", "Directory" })
        {
            using var key = Registry.ClassesRoot.OpenSubKey(path, writable: false);
            if (key?.GetValue("DefaultDropEffect") is int value && Enum.IsDefined(typeof(DefaultDropEffect), value))
            {
                return (DefaultDropEffect)value;
            }
        }

        return DefaultDropEffect.Default;
    }

    private static bool HasAnyValue(RegistryKey key, params string[] valueNames) => valueNames.Any(name => key.GetValue(name) is not null);

    private static string ResolveShellNewDisplayName(RegistryKey mergedClassesRoot, RegistryKey shellNewKey, string extension, string? progId)
    {
        var menuText = ResolveShellNewMenuText(shellNewKey);
        if (!string.IsNullOrWhiteSpace(menuText))
        {
            return menuText;
        }

        if (!string.IsNullOrWhiteSpace(progId))
        {
            using var progIdKey = mergedClassesRoot.OpenSubKey(progId, writable: false);
            var friendly = ShellMetadataResolver.ResolveResourceString(progIdKey?.GetValue("FriendlyTypeName")?.ToString());
            if (!string.IsNullOrWhiteSpace(friendly))
            {
                return friendly;
            }

            var defaultName = ShellMetadataResolver.ResolveResourceString(progIdKey?.GetValue(null)?.ToString());
            if (!string.IsNullOrWhiteSpace(defaultName))
            {
                return defaultName;
            }
        }

        return extension;
    }

    private static string ResolveShellNewMenuText(RegistryKey shellNewKey)
    {
        var value = shellNewKey.GetValue("MenuText")?.ToString();
        if (!IsIndirectResourceString(value))
        {
            return string.Empty;
        }

        return ShellMetadataResolver.ResolveResourceString(value);
    }

    private static bool IsIndirectResourceString(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.TrimStart().StartsWith('@');

    private static (string? IconPath, int IconIndex, string? FallbackPath) ResolveShellNewIcon(RegistryKey mergedClassesRoot, RegistryKey shellNewKey, string extension, string? progId)
    {
        var icon = shellNewKey.GetValue("IconPath")?.ToString();
        if (!string.IsNullOrWhiteSpace(icon))
        {
            var (path, index) = ParseIconLocation(icon);
            return (path, index, extension);
        }

        if (string.Equals(extension, "Folder", StringComparison.OrdinalIgnoreCase))
        {
            return ("imageres.dll", -3, null);
        }

        if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return ("shell32.dll", -16769, extension);
        }

        if (string.IsNullOrWhiteSpace(progId))
        {
            return (null, 0, extension);
        }

        using var iconKey = mergedClassesRoot.OpenSubKey($@"{progId}\DefaultIcon", writable: false);
        var defaultIcon = iconKey?.GetValue(null)?.ToString();
        if (!string.IsNullOrWhiteSpace(defaultIcon))
        {
            var (path, index) = ParseIconLocation(defaultIcon);
            return (path, index, extension);
        }

        return (null, 0, extension);
    }

    private static string GetShellNewCreationKind(RegistryKey key)
    {
        foreach (var name in new[] { "NullFile", "Data", "FileName", "Directory", "Command" })
        {
            if (key.GetValue(name) is not null)
            {
                return name;
            }
        }

        return string.Empty;
    }

    private static bool IsShellNewBeforeSeparator(RegistryKey key, string extension)
    {
        if (new[] { "Folder", ".library-ms", ".lnk" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        using var config = key.OpenSubKey("Config", writable: false);
        return config?.GetValue("BeforeSeparator") is not null;
    }

    private static bool IsBeforeSeparatorEntry(SpecialMenuEntry item) =>
        bool.TryParse(item.Metadata.GetValueOrDefault("BeforeSeparator"), out var beforeSeparator) && beforeSeparator;

    private static SpecialMenuEntry CreateShellNewSeparatorEntry() => new()
    {
        Id = $"{SpecialMenuKind.ShellNew}:Separator",
        Kind = SpecialMenuKind.ShellNew,
        DisplayName = "ShellNewSeparator",
        KeyName = "Separator",
        IsEnabled = true,
        CanEdit = false,
        CanDelete = false,
        CanMove = false,
        Metadata = new Dictionary<string, string>
        {
            ["EntryType"] = "Separator",
            ["LocalizationKey"] = "ShellNewSeparator"
        }
    };

    private static Dictionary<string, int> GetShellNewOrderedExtensions(BackendUserContext context)
    {
        using var userRoot = GetUserRegistryRoot(context, writable: false);
        using var key = userRoot.OpenSubKey(ShellNewOrderPath, writable: false);
        var classes = key?.GetValue("Classes") as string[] ?? [];
        return classes.Select((value, index) => (value, index)).ToDictionary(static item => item.value, static item => item.index, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsRealShellNewEntry(SpecialMenuEntry item) =>
        !string.Equals(item.Metadata.GetValueOrDefault("EntryType"), "Separator", StringComparison.OrdinalIgnoreCase);

    private static bool IsClassicShellNewEntry(SpecialMenuEntry item) =>
        IsRealShellNewEntry(item) && !IsPackagedShellNewItem(item);

    private static bool IsPackagedShellNewItem(SpecialMenuEntry item) =>
        item.Kind == SpecialMenuKind.ShellNew
        && (string.Equals(item.Metadata.GetValueOrDefault("ProviderType"), PackagedShellNewDiscovery.ProviderType, StringComparison.Ordinal)
            || item.Id.StartsWith("shellnew:package:", StringComparison.OrdinalIgnoreCase));

    private static void ThrowIfReadOnlyPackagedShellNew(SpecialMenuEntry item)
    {
        if (IsPackagedShellNewItem(item))
        {
            throw new InvalidOperationException("This ShellNew item is declared by an installed app package and is read-only. ContextMenuMgr will not modify package manifests, file associations, or package state.");
        }
    }

    private static void ThrowIfReadOnlyPackagedShellNewId(string itemId)
    {
        if (itemId.StartsWith("shellnew:package:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This ShellNew item is declared by an installed app package and is read-only. ContextMenuMgr will not modify package manifests, file associations, or package state.");
        }
    }

    private void WriteShellNewOrderClasses(BackendUserContext context, IReadOnlyList<SpecialMenuEntry> allRealItems)
    {
        using var userRoot = GetUserRegistryRoot(context, writable: true);
        using var orderKey = userRoot.CreateSubKey(ShellNewOrderPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open ShellNew order key.");

        var classes = allRealItems.Select(static item => item.KeyName).ToArray();
        orderKey.SetValue(
            "Classes",
            classes,
            RegistryValueKind.MultiString);
        _logger.LogFireAndForget(
            DiagnosticLogFormatter.BuildRegistryOperationLog(
                "WriteShellNewOrderClasses",
                DiagnosticLogFormatter.FormatUserHivePath(context, ShellNewOrderPath),
                "Classes",
                RegistryValueKind.MultiString,
                classes,
                writable: true,
                result: $"Success, Count={classes.Length}, Classes={string.Join(";", classes)}"));
    }

    private static bool ShellNewOrderKeyExists(BackendUserContext context)
    {
        try
        {
            using var userRoot = GetUserRegistryRoot(context, writable: false);
            using var key = userRoot.OpenSubKey(
                ShellNewOrderPath,
                RegistryKeyPermissionCheck.ReadSubTree,
                RegistryRights.ReadPermissions);
            return key is not null;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (SecurityException)
        {
            return true;
        }
    }

    private bool CreateShellNewOrderKeyIfMissing(BackendUserContext context)
    {
        var fullPath = DiagnosticLogFormatter.FormatUserHivePath(context, ShellNewOrderPath);
        if (ShellNewOrderKeyExists(context))
        {
            _logger.LogFireAndForget($"CreateShellNewOrderKeyIfMissing: FullPath={fullPath}, Exists=True, Created=False.");
            return false;
        }

        using var userRoot = GetUserRegistryRoot(context, writable: true);
        using var key = userRoot.CreateSubKey(ShellNewOrderPath, writable: true);
        var created = key is not null;
        _logger.LogFireAndForget($"CreateShellNewOrderKeyIfMissing: FullPath={fullPath}, Exists=False, Created={created}.");
        return created;
    }

    private RegistryKey? OpenShellNewOrderKeyForAclWrite(BackendUserContext context)
    {
        var fullPath = DiagnosticLogFormatter.FormatUserHivePath(context, ShellNewOrderPath);
        const RegistryRights rights = RegistryRights.ReadPermissions | RegistryRights.ChangePermissions;
        _logger.LogFireAndForget(DiagnosticLogFormatter.BuildAclOperationLog("OpenShellNewOrderKeyForAclWrite", fullPath, rights, "PermissionCheck=ReadWriteSubTree"));
        try
        {
            using var userRoot = GetUserRegistryRoot(context, writable: false);
            var key = userRoot.OpenSubKey(
                ShellNewOrderPath,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                rights);
            _logger.LogFireAndForget(DiagnosticLogFormatter.BuildAclOperationLog("OpenShellNewOrderKeyForAclWrite", fullPath, rights, key is null ? "Missing" : "Success"));
            return key;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"OpenShellNewOrderKeyForAclWriteFailed: Sid={context.Sid}, FullPath={fullPath}, Rights={rights}, LegacyMessage={ShellNewLegacyBroadLockMessage}, Exception={ex}");
            throw CreateShellNewLegacyBroadLockException(ex);
        }
    }

    private RegistryKey? OpenShellNewOrderKeyForAclRead(BackendUserContext context)
    {
        var fullPath = DiagnosticLogFormatter.FormatUserHivePath(context, ShellNewOrderPath);
        const RegistryRights rights = RegistryRights.ReadPermissions;
        _logger.LogFireAndForget(DiagnosticLogFormatter.BuildAclOperationLog("OpenShellNewOrderKeyForAclRead", fullPath, rights, "PermissionCheck=ReadSubTree"));
        using var userRoot = GetUserRegistryRoot(context, writable: false);
        var key = userRoot.OpenSubKey(
            ShellNewOrderPath,
            RegistryKeyPermissionCheck.ReadSubTree,
            rights);
        _logger.LogFireAndForget(DiagnosticLogFormatter.BuildAclOperationLog("OpenShellNewOrderKeyForAclRead", fullPath, rights, key is null ? "Missing" : "Success"));
        return key;
    }

    private ShellNewLockChangeResult ApplyShellNewOrderLock(BackendUserContext context)
    {
        var fullPath = DiagnosticLogFormatter.FormatUserHivePath(context, ShellNewOrderPath);
        var keyCreated = CreateShellNewOrderKeyIfMissing(context);

        using var key = OpenShellNewOrderKeyForAclWrite(context)
            ?? throw new InvalidOperationException("Unable to open ShellNew order key for ACL write.");
        RegistrySecurity security;
        try
        {
            security = key.GetAccessControl(AccessControlSections.Access);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"ApplyShellNewOrderLockReadAclFailed: Sid={context.Sid}, FullPath={fullPath}, LegacyMessage={ShellNewLegacyBroadLockMessage}, Exception={ex}");
            throw CreateShellNewLegacyBroadLockException(ex);
        }

        var detectedVersion = GetShellNewLockVersion(security);
        _logger.LogFireAndForget($"ApplyShellNewOrderLockDetected: Sid={context.Sid}, FullPath={fullPath}, LockVersion={detectedVersion}.");
        var removedRules = RemoveMatchingShellNewWorldDenyRules(security).ToArray();
        foreach (var removedRule in removedRules)
        {
            _logger.LogFireAndForget($"ApplyShellNewOrderLockRemovedAce: Sid={context.Sid}, FullPath={fullPath}, RightsRemoved={removedRule.RegistryRights}, Rule={DiagnosticLogFormatter.FormatAclRule(removedRule)}.");
        }

        var denyRule = new RegistryAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            ShellNewOrderLockRights,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);
        security.AddAccessRule(denyRule);
        _logger.LogFireAndForget($"ApplyShellNewOrderLockAddedAce: Sid={context.Sid}, FullPath={fullPath}, RightsAdded={ShellNewOrderLockRights}, Rule={DiagnosticLogFormatter.FormatAclRule(denyRule)}.");

        try
        {
            key.SetAccessControl(security);
            _logger.LogFireAndForget($"ApplyShellNewOrderLockSetAccessControl: Sid={context.Sid}, FullPath={fullPath}, Success=True.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"ApplyShellNewOrderLockSetAccessControlFailed: Sid={context.Sid}, FullPath={fullPath}, LegacyMessage={ShellNewLegacyBroadLockMessage}, Exception={ex}");
            throw CreateShellNewLegacyBroadLockException(ex);
        }

        if (!TryVerifyShellNewOrderLocked(context, out var verificationMessage))
        {
            throw new InvalidOperationException($"ShellNew order lock ACL did not contain expected WorldSid deny rule after SetAccessControl. {verificationMessage}");
        }

        _logger.LogFireAndForget($"ApplyShellNewOrderLockVerification: FullPath={fullPath}, Verification={verificationMessage}.");
        return new ShellNewLockChangeResult(keyCreated, $"OpenRights=ReadPermissions|ChangePermissions. LockVersionBefore={detectedVersion}. RemovedWorldDenyRules={removedRules.Length}. RightsRemoved={FormatRegistryRights(removedRules)}. Applied WorldSid Deny {ShellNewOrderLockRights}. SetAccessControl=True. {verificationMessage}");
    }

    private ShellNewLockChangeResult RemoveShellNewOrderLock(BackendUserContext context)
    {
        var fullPath = DiagnosticLogFormatter.FormatUserHivePath(context, ShellNewOrderPath);
        using var key = OpenShellNewOrderKeyForAclWrite(context);
        if (key is null)
        {
            _logger.LogFireAndForget($"RemoveShellNewOrderLockVerification: Sid={context.Sid}, FullPath={fullPath}, Result=KeyMissing.");
            return new ShellNewLockChangeResult(KeyCreated: false, Message: "ShellNew order key is missing.");
        }

        RegistrySecurity security;
        try
        {
            security = key.GetAccessControl(AccessControlSections.Access);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"RemoveShellNewOrderLockReadAclFailed: Sid={context.Sid}, FullPath={fullPath}, LegacyMessage={ShellNewLegacyBroadLockMessage}, Exception={ex}");
            throw CreateShellNewLegacyBroadLockException(ex);
        }

        var detectedVersion = GetShellNewLockVersion(security);
        _logger.LogFireAndForget($"RemoveShellNewOrderLockDetected: Sid={context.Sid}, FullPath={fullPath}, LockVersion={detectedVersion}.");
        var removedRules = RemoveMatchingShellNewWorldDenyRules(security).ToArray();
        foreach (var removedRule in removedRules)
        {
            _logger.LogFireAndForget($"RemoveShellNewOrderLockRemovedAce: Sid={context.Sid}, FullPath={fullPath}, RightsRemoved={removedRule.RegistryRights}, Rule={DiagnosticLogFormatter.FormatAclRule(removedRule)}.");
        }

        try
        {
            key.SetAccessControl(security);
            _logger.LogFireAndForget($"RemoveShellNewOrderLockSetAccessControl: Sid={context.Sid}, FullPath={fullPath}, Success=True, RemovedWorldDenyRules={removedRules.Length}, RightsRemoved={FormatRegistryRights(removedRules)}.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"RemoveShellNewOrderLockSetAccessControlFailed: Sid={context.Sid}, FullPath={fullPath}, LockVersion={detectedVersion}, LegacyMessage={ShellNewLegacyBroadLockMessage}, Exception={ex}");
            throw CreateShellNewLegacyBroadLockException(ex);
        }

        if (!TryVerifyShellNewOrderUnlocked(context, out var verificationMessage))
        {
            throw new InvalidOperationException($"Failed to verify ShellNew order unlock after removing WorldSid deny rule. {verificationMessage}");
        }

        _logger.LogFireAndForget($"RemoveShellNewOrderLockVerification: Sid={context.Sid}, FullPath={fullPath}, LockVersionBefore={detectedVersion}, RemovedWorldDenyRules={removedRules.Length}, Verification={verificationMessage}.");
        return new ShellNewLockChangeResult(KeyCreated: false, Message: $"OpenRights=ReadPermissions|ChangePermissions. LockVersionBefore={detectedVersion}. RemovedWorldDenyRules={removedRules.Length}. RightsRemoved={FormatRegistryRights(removedRules)}. SetAccessControl=True. {verificationMessage}");
    }

    private static IEnumerable<RegistryAccessRule> RemoveMatchingShellNewWorldDenyRules(RegistrySecurity security)
    {
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<RegistryAccessRule>()
            .Where(IsShellNewWorldDenyRule)
            .ToArray();
        foreach (var rule in rules)
        {
            security.RemoveAccessRuleSpecific(rule);
            yield return rule;
        }
    }

    private static bool IsShellNewWorldDenyRule(RegistryAccessRule rule)
    {
        if (rule.IsInherited || rule.AccessControlType != AccessControlType.Deny)
        {
            return false;
        }

        return IsWorldSid(rule.IdentityReference)
            && (IsShellNewLegacyWorldLockRule(rule) || IsShellNewV2WorldDenyRule(rule));
    }

    private static bool IsShellNewWorldLockRule(RegistryAccessRule rule)
        => IsShellNewLegacyWorldLockRule(rule) || IsShellNewV2WorldLockRule(rule);

    private static bool IsShellNewLegacyWorldLockRule(RegistryAccessRule rule)
    {
        return !rule.IsInherited
            && rule.AccessControlType == AccessControlType.Deny
            && IsWorldSid(rule.IdentityReference)
            && HasAnyRegistryRight(rule.RegistryRights, RegistryRights.WriteKey);
    }

    private static bool IsShellNewV2WorldLockRule(RegistryAccessRule rule)
    {
        return !rule.IsInherited
            && rule.AccessControlType == AccessControlType.Deny
            && IsWorldSid(rule.IdentityReference)
            && (rule.RegistryRights & RegistryRights.SetValue) == RegistryRights.SetValue
            && (rule.RegistryRights & RegistryRights.CreateSubKey) == RegistryRights.CreateSubKey
            && (rule.RegistryRights & RegistryRights.Delete) == RegistryRights.Delete;
    }

    private static bool IsShellNewV2WorldDenyRule(RegistryAccessRule rule)
    {
        return !rule.IsInherited
            && rule.AccessControlType == AccessControlType.Deny
            && IsWorldSid(rule.IdentityReference)
            && HasAnyRegistryRight(
                rule.RegistryRights,
                RegistryRights.SetValue,
                RegistryRights.CreateSubKey,
                RegistryRights.Delete);
    }

    private static bool HasAnyRegistryRight(RegistryRights actual, params RegistryRights[] requiredRights) =>
        requiredRights.Any(right => (actual & right) != 0);

    private static string GetShellNewLockVersion(RegistrySecurity security)
    {
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<RegistryAccessRule>().ToArray();
        if (rules.Any(IsShellNewLegacyWorldLockRule))
        {
            return "legacy-writekey";
        }

        return rules.Any(IsShellNewV2WorldLockRule) ? "v2-narrow" : "none";
    }

    private static string FormatRegistryRights(IEnumerable<RegistryAccessRule> rules)
    {
        var values = rules.Select(static rule => rule.RegistryRights.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return values.Length == 0 ? "none" : string.Join("|", values);
    }

    private static InvalidOperationException CreateShellNewLegacyBroadLockException(Exception innerException)
        => new(ShellNewLegacyBroadLockMessage, innerException);

    private static bool IsWorldSid(IdentityReference identity)
    {
        try
        {
            var sid = identity as SecurityIdentifier
                ?? identity.Translate(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return sid?.IsWellKnown(WellKnownSidType.WorldSid) == true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsShellNewOrderLocked(BackendUserContext context)
    {
        if (TryReadShellNewOrderLockStateStatic(context, out var locked, out _))
        {
            return locked;
        }

        return locked;
    }

    private static bool TryReadShellNewOrderLockStateStatic(BackendUserContext context, out bool locked, out string message)
    {
        try
        {
            using var userRoot = GetUserRegistryRoot(context, writable: false);
            using var key = userRoot.OpenSubKey(
                ShellNewOrderPath,
                RegistryKeyPermissionCheck.ReadSubTree,
                RegistryRights.ReadPermissions);
            if (key is null)
            {
                locked = false;
                message = "ShellNew order key is missing.";
                return true;
            }

            var security = key.GetAccessControl(AccessControlSections.Access);
            var lockVersion = GetShellNewLockVersion(security);
            locked = !string.Equals(lockVersion, "none", StringComparison.OrdinalIgnoreCase);
            message = $"ReadableLockVersion={lockVersion}.";
            return true;
        }
        catch (SecurityException ex)
        {
            locked = true;
            message = $"UnreadableSecurityException={ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            locked = true;
            message = $"UnreadableUnauthorizedAccessException={ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            locked = false;
            message = $"UnexpectedAclReadFailure={ex.Message}";
            return false;
        }
    }

    private bool TryVerifyShellNewOrderLocked(BackendUserContext context, out string message)
    {
        if (TryReadShellNewOrderLockState(context, out var locked, out message))
        {
            if (locked)
            {
                message = "Verification=ReadableFoundWorldSidDeny.";
                return true;
            }

            message = "Verification=ReadableNoWorldSidDeny.";
            return false;
        }

        message = "Verification=UnreadableAfterLockTreatingAsLocked.";
        return locked;
    }

    private bool TryVerifyShellNewOrderUnlocked(BackendUserContext context, out string message)
    {
        if (TryReadShellNewOrderLockState(context, out var locked, out message))
        {
            if (!locked)
            {
                message = "ReadableUnlocked.";
                return true;
            }

            message = "ReadableStillLocked.";
            return false;
        }

        message = "UnreadableAfterReset.";
        return false;
    }

    private bool TryReadShellNewOrderLockState(BackendUserContext context, out bool locked, out string message)
    {
        var fullPath = DiagnosticLogFormatter.FormatUserHivePath(context, ShellNewOrderPath);
        _logger.LogFireAndForget($"TryReadShellNewOrderLockStateStart: FullPath={fullPath}, ReadPermissionsAttempt=True.");
        try
        {
            using var key = OpenShellNewOrderKeyForAclRead(context);
            if (key is null)
            {
                locked = false;
                message = "ShellNew order key is missing.";
                return true;
            }

            var security = key.GetAccessControl(AccessControlSections.Access);
            var explicitDenyRules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<RegistryAccessRule>()
                .Where(static rule => !rule.IsInherited && rule.AccessControlType == AccessControlType.Deny)
                .ToArray();
            var lockVersion = GetShellNewLockVersion(security);
            locked = !string.Equals(lockVersion, "none", StringComparison.OrdinalIgnoreCase);
            message = $"ReadableLockVersion={lockVersion}.";
            foreach (var rule in explicitDenyRules)
            {
                _logger.LogFireAndForget($"TryReadShellNewOrderLockStateExplicitDenyAce: FullPath={fullPath}, Rule={DiagnosticLogFormatter.FormatAclRule(rule)}.");
            }

            _logger.LogFireAndForget($"TryReadShellNewOrderLockStateEnd: FullPath={fullPath}, LockVersion={lockVersion}, Result={message}");
            return true;
        }
        catch (SecurityException ex)
        {
            Debug.WriteLine($"[IsShellNewOrderLocked] SecurityException when reading ACL: {ex.Message}");
            locked = true;
            message = $"UnreadableSecurityException={ex.Message}";
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"TryReadShellNewOrderLockStateEnd: FullPath={fullPath}, Result=UnreadableSecurityException, Exception={ex}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Debug.WriteLine($"[IsShellNewOrderLocked] UnauthorizedAccessException when reading ACL: {ex.Message}");
            locked = true;
            message = $"UnreadableUnauthorizedAccessException={ex.Message}";
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"TryReadShellNewOrderLockStateEnd: FullPath={fullPath}, Result=UnreadableUnauthorizedAccessException, Exception={ex}");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IsShellNewOrderLocked] Unexpected exception when reading ACL: {ex.Message}");
            locked = false;
            message = $"UnexpectedAclReadFailure={ex.Message}";
            _logger.LogFireAndForget(RuntimeLogLevel.Warning, $"TryReadShellNewOrderLockStateEnd: FullPath={fullPath}, Result=UnexpectedAclReadFailure, Exception={ex}");
            return false;
        }
    }

    private static bool CanWriteShellNewOrder(BackendUserContext context)
    {
        try
        {
            using var userRoot = GetUserRegistryRoot(context, writable: false);
            using var key = userRoot.OpenSubKey(ShellNewOrderPath, writable: true);
            if (key is not null)
            {
                return true;
            }

            var parentPath = ShellNewOrderPath[..ShellNewOrderPath.LastIndexOf('\\')];
            using var parent = userRoot.OpenSubKey(parentPath, writable: true);
            return parent is not null;
        }
        catch (SecurityException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static RegistryKey GetWritableUserClassesRoot(BackendUserContext context)
    {
        using var userRoot = GetUserRegistryRoot(context, writable: true);
        return userRoot.CreateSubKey(UserClassesPath, writable: true)
            ?? throw new InvalidOperationException("The current user's registry hive is not available.");
    }

    private static RegistryKey? OpenUserClassesSubKey(BackendUserContext context, string relativePath, bool writable)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        using var userRoot = GetUserRegistryRoot(context, writable);
        return userRoot.OpenSubKey($@"{UserClassesPath}\{relativePath.Trim('\\')}", writable);
    }

    private static RegistryKey? GetWritableUserClassesSubKey(BackendUserContext context, string relativePath, bool create)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        using var userRoot = GetUserRegistryRoot(context, writable: true);
        var subPath = $@"{UserClassesPath}\{relativePath.Trim('\\')}";
        return create
            ? userRoot.CreateSubKey(subPath, writable: true)
            : userRoot.OpenSubKey(subPath, writable: true);
    }

    private static bool TryGetClassesRelativePath(string registryPath, BackendUserContext context, out string relativePath)
    {
        var prefixes = new[]
        {
            $@"HKEY_USERS\{context.Sid}\{UserClassesPath}",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes",
            "HKEY_CLASSES_ROOT"
        };

        foreach (var prefix in prefixes)
        {
            if (registryPath.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = string.Empty;
                return true;
            }

            if (registryPath.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
            {
                relativePath = registryPath[(prefix.Length + 1)..];
                return true;
            }
        }

        relativePath = string.Empty;
        return false;
    }

    private static string GetShellNewExtensionFromRegistryPath(string registryPath, BackendUserContext context)
    {
        if (!TryGetClassesRelativePath(registryPath, context, out var relativePath))
        {
            return string.Empty;
        }

        foreach (var part in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith(".", StringComparison.Ordinal) || string.Equals(part, "Folder", StringComparison.OrdinalIgnoreCase))
            {
                return part;
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<ClassesRootSpec> GetClassesRootSpecs(BackendUserContext context)
    {
        var specs = new List<ClassesRootSpec>();
        try
        {
            specs.Add(GetUserClassesRootSpec(context));
        }
        catch
        {
        }

        try
        {
            var machineClasses = Registry.LocalMachine.OpenSubKey(MachineClassesPath, writable: false);
            if (machineClasses is not null)
            {
                specs.Add(new ClassesRootSpec(machineClasses, @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes", "System"));
            }
        }
        catch
        {
        }

        return specs;
    }

    private static IReadOnlyList<ClassesRootSpec> GetClassesRootSpecsSafe(BackendUserContext context)
    {
        return GetClassesRootSpecs(context);
    }

    private static ClassesRootSpec GetUserClassesRootSpec(BackendUserContext context)
    {
        var userBaseKey = GetUserRegistryRoot(context, writable: true);
        var root = userBaseKey.OpenSubKey(UserClassesPath, writable: false)
            ?? userBaseKey.CreateSubKey(UserClassesPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open caller user classes.");

        return new ClassesRootSpec(root, $@"HKEY_USERS\{context.Sid}\Software\Classes", "User");
    }

    private static ClassesRootSpec GetClassesRootSpecForPath(string registryPath, BackendUserContext context)
    {
        foreach (var spec in GetClassesRootSpecs(context))
        {
            if (registryPath.StartsWith(spec.RegistryPrefix + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return spec;
            }
        }

        return new ClassesRootSpec(Registry.ClassesRoot, "HKEY_CLASSES_ROOT", "Merged");
    }

    private static BackendUserContext RequireUserContext(BackendUserContext? context) =>
        context ?? throw new InvalidOperationException("This operation requires an interactive user context.");

    private static RegistryKey GetUserRegistryRoot(BackendUserContext context, bool writable)
    {
        if (string.IsNullOrWhiteSpace(context.Sid))
        {
            throw new InvalidOperationException("The frontend user SID is not available.");
        }

        return Registry.Users.OpenSubKey(context.Sid, writable)
            ?? throw new InvalidOperationException("The current user's registry hive is not available.");
    }

    private static string GetSendToPath(BackendUserContext context) => context.GetSendToPath();

    private static string GetWinXPath(BackendUserContext context) => context.GetWinXPath();

    private static void AddKnownShellNewLocalization(string extension, IDictionary<string, string> metadata)
    {
        if (string.Equals(extension, "Folder", StringComparison.OrdinalIgnoreCase))
        {
            metadata["LocalizationKey"] = "ShellNewFolder";
        }
        else if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            metadata["LocalizationKey"] = "ShellNewShortcut";
        }
        else if (string.Equals(extension, ".library-ms", StringComparison.OrdinalIgnoreCase))
        {
            metadata["LocalizationKey"] = "ShellNewLibrary";
        }
    }

    private static string StripAcceleratorPrefix(string value) => value.Replace("&", string.Empty, StringComparison.Ordinal);

    private static string StripWinXOrderPrefix(string value)
    {
        var text = value.Trim();
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '-')
            {
                continue;
            }

            var prefix = text[..index].Trim();
            if (IsWinXOrderPrefix(prefix))
            {
                var remainder = text[(index + 1)..].TrimStart(' ');
                var nextHyphen = remainder.IndexOf('-');
                if (nextHyphen > 0 && IsWinXOrderPrefix(remainder[..nextHyphen].Trim()))
                {
                    remainder = remainder[(nextHyphen + 1)..].TrimStart(' ');
                }

                return remainder.TrimStart('-');
            }

            break;
        }

        return value;
    }

    private static bool IsWinXOrderPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !char.IsDigit(prefix[0]))
        {
            return false;
        }

        var hasDigit = false;
        foreach (var c in prefix)
        {
            if (char.IsDigit(c))
            {
                hasDigit = true;
                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                continue;
            }

            return false;
        }

        return hasDigit;
    }

    private static (string? IconPath, int IconIndex) ParseIconLocation(string? iconLocation)
    {
        if (string.IsNullOrWhiteSpace(iconLocation))
        {
            return (null, 0);
        }

        var value = Environment.ExpandEnvironmentVariables(iconLocation.Trim().Trim('"'));
        var commaIndex = value.LastIndexOf(',');
        if (commaIndex > 0 && int.TryParse(value[(commaIndex + 1)..].Trim(), out var iconIndex))
        {
            return (value[..commaIndex].Trim().Trim('"'), iconIndex);
        }

        return (value, 0);
    }

    private static void RestoreDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        if (Directory.Exists(destination))
        {
            File.SetAttributes(destination, FileAttributes.Normal);
            Directory.Delete(destination, recursive: true);
        }

        CopyDirectory(source, destination);
    }

    private static void RestoreWinXGroupWithBackup(string source, string destination, string winXPath)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        EnsurePathUnder(destination, winXPath);
        if (IsSamePath(destination, winXPath))
        {
            throw new UnauthorizedAccessException("Refusing to restore defaults over the Win+X root folder.");
        }

        var groupName = ValidateWinXGroupName(Path.GetFileName(destination));
        var backupRoot = Path.Combine(winXPath, BackupFolderName);
        EnsurePathUnder(backupRoot, winXPath);
        Directory.CreateDirectory(backupRoot);

        var backupPath = Path.Combine(backupRoot, $"{groupName}-{DateTime.Now:yyyyMMddHHmmssfff}");
        var hasBackup = false;
        try
        {
            if (Directory.Exists(destination))
            {
                ClearAttributesRecursive(destination);
                Directory.Move(destination, backupPath);
                hasBackup = true;
            }

            CopyDirectory(source, destination);
        }
        catch
        {
            if (Directory.Exists(destination))
            {
                ClearAttributesRecursive(destination);
                Directory.Delete(destination, recursive: true);
            }

            if (hasBackup && Directory.Exists(backupPath))
            {
                Directory.Move(backupPath, destination);
            }

            throw;
        }
    }

    private static void CopyRegistryKey(RegistryKey source, RegistryKey target)
    {
        foreach (var name in source.GetValueNames())
        {
            var value = source.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var kind = source.GetValueKind(name);
            if (value is not null)
            {
                target.SetValue(name, value, kind);
            }
        }

        foreach (var subKeyName in source.GetSubKeyNames())
        {
            using var sourceSubKey = source.OpenSubKey(subKeyName, writable: false);
            if (sourceSubKey is not null)
            {
                using var targetSubKey = target.CreateSubKey(subKeyName, writable: true);
                if (targetSubKey is not null)
                {
                    CopyRegistryKey(sourceSubKey, targetSubKey);
                }
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        File.SetAttributes(destination, File.GetAttributes(source));
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void DeleteFileSystemItem(string? path, string allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        EnsurePathUnder(path, allowedRoot);
        if (Directory.Exists(path))
        {
            ClearAttributesRecursive(path);
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            DesktopIniStore.DeleteLocalizedFileName(path);
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    private static void ClearAttributesRecursive(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        foreach (var directory in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Normal);
        }

        File.SetAttributes(path, FileAttributes.Normal);
    }

    private static void MoveRegistryKey(string sourcePath, string targetPath, BackendUserContext? context = null, FileLogger? logger = null, bool requireTargetAbsent = false)
    {
        logger?.LogFireAndForget($"MoveRegistryKeyStart: SourcePath={sourcePath}, TargetPath={targetPath}, Sid={DiagnosticLogFormatter.FormatSid(context)}.");
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogFireAndForget($"MoveRegistryKeySkipped: SourcePath={sourcePath}, TargetPath={targetPath}, Sid={DiagnosticLogFormatter.FormatSid(context)}, Reason=SamePath.");
            return;
        }

        try
        {
            MoveRegistryKeyCore(sourcePath, targetPath, context, logger, requireTargetAbsent);
            logger?.LogFireAndForget($"MoveRegistryKeyEnd: SourcePath={sourcePath}, TargetPath={targetPath}, Sid={DiagnosticLogFormatter.FormatSid(context)}, Result=Success.");
        }
        catch (UnauthorizedAccessException)
        {
            EnableSecurityPrivilege();
            logger?.LogFireAndForget($"MoveRegistryKeyRetryWithSecurityPrivilege: SourcePath={sourcePath}, TargetPath={targetPath}, Sid={DiagnosticLogFormatter.FormatSid(context)}.");
            MoveRegistryKeyCore(sourcePath, targetPath, context, logger, requireTargetAbsent);
            logger?.LogFireAndForget($"MoveRegistryKeyEnd: SourcePath={sourcePath}, TargetPath={targetPath}, Sid={DiagnosticLogFormatter.FormatSid(context)}, Result=SuccessAfterPrivilege.");
        }
    }

    private static void MoveRegistryKeyCore(string sourcePath, string targetPath, BackendUserContext? context = null, FileLogger? logger = null, bool requireTargetAbsent = false)
    {
        var (sourceRoot, sourceSubPath) = SplitRegistryPath(sourcePath, context);
        var (targetRoot, targetSubPath) = SplitRegistryPath(targetPath, context);
        logger?.LogFireAndForget($"MoveRegistryKeyCore: SourcePath={sourcePath}, TargetPath={targetPath}, Sid={DiagnosticLogFormatter.FormatSid(context)}, SourceRoot={sourceRoot.Name}, SourceSubPath={sourceSubPath}, TargetRoot={targetRoot.Name}, TargetSubPath={targetSubPath}.");
        using var source = OpenRegistryKey(sourcePath, writable: false, context)
            ?? throw new InvalidOperationException($"Unable to open {sourcePath}.");
        using var existingTarget = requireTargetAbsent
            ? OpenRegistryKey(targetPath, writable: false, context)
            : null;
        if (existingTarget is not null)
        {
            throw new InvalidOperationException($"Unable to move ShellNew registration because destination already exists: {targetPath}");
        }

        CreateRegistryKeyCopy(source, targetPath, context, logger);
        DeleteRegistryTree(sourcePath, context, logger);
    }

    private static void CreateRegistryKeyCopy(RegistryKey source, string targetPath, BackendUserContext? context = null, FileLogger? logger = null)
    {
        using var target = CreateRegistryKey(targetPath, context)
            ?? throw new InvalidOperationException($"Unable to create {targetPath}.");
        logger?.LogFireAndForget($"CreateRegistryKeyCopy: SourceName={source.Name}, TargetPath={targetPath}, Sid={DiagnosticLogFormatter.FormatSid(context)}, ValueCount={source.GetValueNames().Length}, SubKeyCount={source.GetSubKeyNames().Length}.");
        foreach (var valueName in source.GetValueNames())
        {
            target.SetValue(valueName, source.GetValue(valueName)!, source.GetValueKind(valueName));
            logger?.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("CreateRegistryKeyCopySetValue", targetPath, valueName, source.GetValueKind(valueName), source.GetValue(valueName), writable: true, result: "Success"));
        }

        foreach (var subKeyName in source.GetSubKeyNames())
        {
            using var subKey = source.OpenSubKey(subKeyName, writable: false);
            if (subKey is not null)
            {
                CreateRegistryKeyCopy(subKey, $@"{targetPath}\{subKeyName}", context, logger);
            }
        }
    }

    private static void DeleteRegistryTree(string? fullPath, BackendUserContext? context = null, FileLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        var (root, subPath) = SplitRegistryPath(fullPath, context);
        logger?.LogFireAndForget($"DeleteRegistryTreeStart: FullPath={fullPath}, Sid={DiagnosticLogFormatter.FormatSid(context)}, Root={root.Name}, SubPath={subPath}.");
        var parentPath = subPath.Contains('\\') ? subPath[..subPath.LastIndexOf('\\')] : string.Empty;
        var keyName = subPath.Contains('\\') ? subPath[(subPath.LastIndexOf('\\') + 1)..] : subPath;
        using var parent = root.OpenSubKey(parentPath, writable: true);
        parent?.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
        logger?.LogFireAndForget($"DeleteRegistryTreeEnd: FullPath={fullPath}, Sid={DiagnosticLogFormatter.FormatSid(context)}, Root={root.Name}, SubPath={subPath}, Result=Success.");
    }

    private static void DeleteRegistryValue(RegistryKey root, string subPath, string valueName, FileLogger? logger = null)
    {
        using var key = root.OpenSubKey(subPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
        logger?.LogFireAndForget(DiagnosticLogFormatter.BuildRegistryOperationLog("DeleteRegistryValue", $@"{root.Name}\{subPath}", valueName, null, null, writable: true, result: key is null ? "MissingKey" : "Success"));
    }

    private static RegistryKey? OpenRegistryKey(string? fullPath, bool writable, BackendUserContext? context = null)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        var (root, subPath) = SplitRegistryPath(fullPath, context);
        return root.OpenSubKey(subPath, writable);
    }

    private static bool RegistryKeyExists(string fullPath, BackendUserContext context)
    {
        using var key = OpenRegistryKey(fullPath, writable: false, context);
        return key is not null;
    }

    private static RegistryKey? CreateRegistryKey(string fullPath, BackendUserContext? context = null)
    {
        var (root, subPath) = SplitRegistryPath(fullPath, context);
        return root.CreateSubKey(subPath, writable: true);
    }

    private static (RegistryKey Root, string SubPath) SplitRegistryPath(string fullPath, BackendUserContext? context = null)
    {
        var normalized = fullPath.Trim().Replace('/', '\\').Trim('\\');
        var separator = normalized.IndexOf('\\');
        var rootName = separator >= 0 ? normalized[..separator] : normalized;
        var subPath = separator >= 0 ? normalized[(separator + 1)..] : string.Empty;
        if (rootName.Equals("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) || rootName.Equals("HKCU", StringComparison.OrdinalIgnoreCase))
        {
            return context is null
                ? (Registry.CurrentUser, subPath)
                : (Registry.Users, $@"{context.Sid}\{subPath}");
        }

        if ((rootName.Equals("HKEY_USERS", StringComparison.OrdinalIgnoreCase) || rootName.Equals("HKU", StringComparison.OrdinalIgnoreCase))
            && context is not null
            && !subPath.Equals(context.Sid, StringComparison.OrdinalIgnoreCase)
            && !subPath.StartsWith(context.Sid + "\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The registry path is outside the authenticated caller user hive.");
        }

        var root = rootName.ToUpperInvariant() switch
        {
            "HKEY_CLASSES_ROOT" or "HKCR" => Registry.ClassesRoot,
            "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
            "HKEY_USERS" or "HKU" => Registry.Users,
            _ => throw new InvalidOperationException($"Unsupported registry root: {rootName}")
        };

        return (root, subPath);
    }

    private static string GetSiblingShellNewPath(string keyPath, string siblingName)
    {
        var parent = keyPath[..keyPath.LastIndexOf('\\')];
        return $@"{parent}\{siblingName}";
    }

    private static string NormalizeExtension(string value)
    {
        var extension = value.Trim();
        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }

        return extension.ToLowerInvariant();
    }

    private static string GetFullNormalizedPath(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));

    private void LogWinXOperation(string operation, BackendUserContext context, string? targetPath, string winXPath)
    {
        var ensurePathUnderResult = "NotChecked";
        if (!string.IsNullOrWhiteSpace(targetPath) && (Path.IsPathRooted(targetPath) || targetPath.Contains('\\', StringComparison.Ordinal) || targetPath.Contains('/', StringComparison.Ordinal)))
        {
            try
            {
                EnsurePathUnder(targetPath, winXPath);
                ensurePathUnderResult = "UnderWinXRoot";
            }
            catch (Exception ex)
            {
                ensurePathUnderResult = ex.GetType().Name;
            }
        }

        _logger.LogFireAndForget(
            $"WinXOperation: Operation={operation}, Sid={context.Sid}, ProfilePath={context.ProfilePath}, LocalAppDataPath={context.LocalAppDataPath}, WinXPath={winXPath}, TargetPath={targetPath}, EnsurePathUnderResult={ensurePathUnderResult}, HasherMode=HardCodedPKEYs.");
    }

    private static bool IsSamePath(string path, string otherPath)
    {
        var fullPath = GetFullNormalizedPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var fullOtherPath = GetFullNormalizedPath(otherPath).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(fullPath, fullOtherPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsurePathUnder(string path, string root)
    {
        var fullPath = GetFullNormalizedPath(path);
        var fullRoot = GetFullNormalizedPath(root);
        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Path is outside the allowed user menu root: {fullPath}");
        }
    }

    private static string ValidatePlainDirectoryName(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('/', StringComparison.Ordinal)
            || value == "."
            || value == ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"{label} must be a simple directory name.");
        }

        return value.Trim();
    }

    private static string RemoveIllegalChars(string fileName)
    {
        foreach (var c in Path.GetInvalidFileNameChars().Concat(['\\', ':']))
        {
            fileName = fileName.Replace(c.ToString(), string.Empty, StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(fileName) ? "Item" : fileName.Trim();
    }

    private static string SanitizeKeyName(string keyName) => keyName.Replace("\\", string.Empty, StringComparison.Ordinal).Trim();

    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var index = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(directory, $"{name}{index}{extension}");
            index++;
        }
        while (File.Exists(candidate) || Directory.Exists(candidate));

        return candidate;
    }

    private static string GetUniqueDirectoryPath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return path;
        }

        var parent = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        var index = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(parent, $"{name}{index}");
            index++;
        }
        while (Directory.Exists(candidate) || File.Exists(candidate));

        return candidate;
    }

    private static void SetOptionalValue(RegistryKey key, string valueName, string? value)
    {
        if (value is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(valueName, value, RegistryValueKind.String);
        }
    }

    private static void SetOptionalRegistryValue(string path, string valueName, string? value, BackendUserContext? context = null)
    {
        if (value is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            using var key = OpenRegistryKey(path, writable: true, context);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        else
        {
            using var key = CreateRegistryKey(path, context)
                ?? throw new InvalidOperationException($"Unable to open {path}.");
            key.SetValue(valueName, value, RegistryValueKind.String);
        }
    }

    private static string EncodeId(SpecialMenuKind kind, string path) =>
        $"{kind}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(path))}";

    private static string DecodeId(string id)
    {
        var index = id.IndexOf(':');
        if (index < 0)
        {
            return id;
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(id[(index + 1)..]));
    }

    private static bool IsDynamicSendToDrive(SpecialMenuEntry item)
        => item.Kind == SpecialMenuKind.SendTo && IsDynamicSendToDriveId(item.Id);

    private static bool IsDynamicSendToDriveId(string? id)
        => !string.IsNullOrWhiteSpace(id)
           && id.StartsWith(DynamicSendToDriveIdPrefix, StringComparison.OrdinalIgnoreCase);

    private static PipeResponse Success(string message, SpecialMenuEntry? item, Guid? operationId) => new()
    {
        Success = true,
        Message = message,
        SpecialItem = item,
        ClientOperationId = operationId
    };

    private static PipeResponse Failure(string message, Guid? operationId) => new()
    {
        Success = false,
        Message = message,
        ClientOperationId = operationId
    };

    private sealed record ClassesRootSpec(RegistryKey Root, string RegistryPrefix, string SourceScope);

    private sealed record ShellNewProgIdResolution(string ProgId, string Source, ShellNewProgIdSourceKind SourceKind);

    private enum ShellNewProgIdSourceKind
    {
        ExtensionDefault,
        UserChoice,
        OpenWithProgids
    }

    private static bool EnableSecurityPrivilege()
    {
        try
        {
            IntPtr tokenHandle;
            var processHandle = Process.GetCurrentProcess().SafeHandle;
            if (!OpenProcessToken(processHandle.DangerousGetHandle(), TOKEN_ADJUST_PRIVILEGES | TOKEN_READ, out tokenHandle))
            {
                return false;
            }

            try
            {
                var privilege = new LUID_AND_ATTRIBUTES
                {
                    Luid = new LUID(),
                    Attributes = SE_PRIVILEGE_ENABLED
                };

                if (!LookupPrivilegeValue(null, SE_SECURITY_NAME, out privilege.Luid))
                {
                    return false;
                }

                var privileges = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = [privilege]
                };

                var length = 0u;
                if (!AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0u, IntPtr.Zero, ref length))
                {
                    return false;
                }

                return Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        catch
        {
            return false;
        }
    }

    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_READ = 0x0008;
    private const uint ERROR_NOT_ALL_ASSIGNED = 1300;

    private const string SE_SECURITY_NAME = "SeSecurityPrivilege";

    private sealed record ShellNewLockChangeResult(bool KeyCreated, string Message);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public LUID_AND_ATTRIBUTES[] Privileges;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool LookupPrivilegeValue(string? systemName, string privilegeName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, ref uint returnLength);
}
