using System.Text.Json;
using ContextMenuMgr.Contracts;
using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

/// <summary>One user-scoped blocked-value control domain, shared by manual and monitor mutations.</summary>
public sealed class Windows11BlocksService(FileLogger logger)
{
    internal const string BlockedPath = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string OwnershipPrefix = "ContextMenuMgrPlus.Win11Blocked.v1|";

    internal static Windows11BlockMutation CreateMutation(
        string handlerClsid, BackendUserContext? context, bool enable, FileLogger logger, bool restoreMachine = false)
    {
        if (!Guid.TryParse(handlerClsid, out var clsid))
            throw new InvalidOperationException("A valid handler CLSID is required.");
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, Windows11ClsidImpactIndex.NativeView);
        using var user = OpenUser(users, context, writable: true);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Windows11ClsidImpactIndex.NativeView);
        using var machineBlocked = machine.OpenSubKey(BlockedPath);
        if (enable && MatchingNames(machineBlocked, clsid).Length > 0 && !restoreMachine)
            throw new InvalidOperationException("This menu is turned off for all users. Refresh and try restoring it again.");
        using var blocked = user.CreateSubKey(BlockedPath, writable: true)
            ?? throw new InvalidOperationException("The user blocked list is unavailable.");
        return CreateMutation(blocked, clsid, context!.Sid, enable, logger, restoreMachine ? machineBlocked : null);
    }

    internal static Windows11BlockMutation CreateMutation(
        RegistryKey blocked, Guid clsid, string sid, bool enable, FileLogger logger, RegistryKey? machineBlocked = null)
    {
        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException("An authenticated frontend user SID is required.");
        var marker = OwnershipPrefix + sid + "|" + clsid.ToString("B");
        var mutation = new Windows11BlockMutation(clsid, sid, enable, marker);
        mutation.AddTarget(blocked);
        if (enable && machineBlocked is not null) mutation.AddTarget(machineBlocked);
        logger.LogFireAndForget(RuntimeLogLevel.Warning,
            $"Win11BlockedMutationPlan: Sid={sid}, Clsid={clsid:B}, Enable={enable}, Before={JsonSerializer.Serialize(mutation.Backups)}, ExplorerValidated=False.");
        return mutation;
    }

    internal static Windows11BlockedState ReadState(string handlerClsid, string userSid)
    {
        var clsid = Guid.Parse(handlerClsid);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Windows11ClsidImpactIndex.NativeView);
        using var machineBlocked = machine.OpenSubKey(BlockedPath);
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, Windows11ClsidImpactIndex.NativeView);
        using var user = OpenUser(users, userSid);
        using var userBlocked = user.OpenSubKey(BlockedPath);
        var userNames = MatchingNames(userBlocked, clsid);
        return new(MatchingNames(machineBlocked, clsid).Length > 0, userNames.Length > 0);
    }

    public async Task<PipeResponse> GetWin11BlockedItemsAsync(
        Guid? operationId, BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        try
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, Windows11ClsidImpactIndex.NativeView);
            using var user = OpenUser(users, userContext);
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Windows11ClsidImpactIndex.NativeView);
            var items = ReadItems(machine, "Machine").Concat(ReadItems(user, "User")).ToArray();
            return new PipeResponse { Success = true, ClientOperationId = operationId, Win11BlockedItems = items };
        }
        catch (Exception ex)
        {
            await logger.LogAsync(RuntimeLogLevel.Warning, $"Win11BlockedReadFailed: {ex}", cancellationToken);
            return new PipeResponse { Success = false, Message = ex.Message, ClientOperationId = operationId };
        }
    }

    private static Win11BlockedItem[] ReadItems(RegistryKey root, string scope)
    {
        using var blocked = root.OpenSubKey(BlockedPath);
        return blocked?.GetValueNames().Where(name => Guid.TryParse(name, out _))
            .Select(name => new Win11BlockedItem { Clsid = name, Scope = scope,
                DisplayName = blocked.GetValue(name)?.ToString() ?? string.Empty }).ToArray() ?? [];
    }

    private static string[] MatchingNames(RegistryKey? blocked, Guid clsid) => blocked?.GetValueNames()
        .Where(name => Guid.TryParse(name, out var parsed) && parsed == clsid).ToArray() ?? [];

    private static RegistryKey OpenUser(RegistryKey users, BackendUserContext? context, bool writable = false)
        => OpenUser(users, context?.Sid ?? string.Empty, writable);

    private static RegistryKey OpenUser(RegistryKey users, string sid, bool writable = false)
    {
        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException("An authenticated frontend user SID is required.");
        return users.OpenSubKey(sid, writable)
            ?? throw new InvalidOperationException("The frontend user's registry hive is not loaded.");
    }
}
