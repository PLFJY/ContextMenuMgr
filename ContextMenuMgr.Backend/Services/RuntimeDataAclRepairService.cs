using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ContextMenuMgr.Backend.Services;

internal sealed record RuntimeDataAclRepairResult(bool Success, string Code, string Detail);

internal static class RuntimeDataAclRepairService
{
    private const int MaxFailureDetails = 5;
    private static readonly SecurityIdentifier BuiltinUsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier AuthenticatedUsersSid = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier WorldSid = new(WellKnownSidType.WorldSid, null);

    public static RuntimeDataAclRepairResult RepairRuntimeDataDirectory(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new RuntimeDataAclRepairResult(false, "UNSUPPORTED_PLATFORM", "Runtime data ACL repair is only supported on Windows.");
        }

        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return new RuntimeDataAclRepairResult(false, "INVALID_RUNTIME_DATA_DIRECTORY", "Runtime data directory path is empty.");
        }

        var repairedCount = 0;
        var failedCount = 0;
        var failures = new List<string>();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(rootDirectory);
            RepairDirectoryAcl(rootDirectory);
            repairedCount++;
        }
        catch (OperationCanceledException)
        {
            return new RuntimeDataAclRepairResult(false, "CANCELLED", "Runtime data ACL repair was cancelled before the root directory was repaired.");
        }
        catch (Exception ex) when (IsRepairException(ex))
        {
            return new RuntimeDataAclRepairResult(
                false,
                "ROOT_REPAIR_FAILED",
                $"Failed to repair runtime data root '{rootDirectory}': {ex.GetType().Name}: {ex.Message}");
        }

        var pendingDirectories = new Stack<string>();
        // Materialize both siblings before logger/DI initialization. Existing protected child
        // ACLs are repaired by the same traversal below; new files inherit Users Modify.
        foreach (var name in new[] { "Logs", "Locks" })
        {
            var directory = Path.Combine(rootDirectory, name);
            try { Directory.CreateDirectory(directory); }
            catch (Exception ex) when (IsRepairException(ex))
            {
                AddFailure(ref failedCount, failures, directory, ex);
            }
        }
        pendingDirectories.Push(rootDirectory);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Pop();

            foreach (var filePath in EnumerateFileSystemEntries(
                         () => Directory.EnumerateFiles(currentDirectory, "*", SearchOption.TopDirectoryOnly),
                         currentDirectory,
                         "files",
                         ref failedCount,
                         failures))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryRepairFile(filePath, ref repairedCount, ref failedCount, failures))
                {
                    continue;
                }
            }

            foreach (var childDirectory in EnumerateFileSystemEntries(
                         () => Directory.EnumerateDirectories(currentDirectory, "*", SearchOption.TopDirectoryOnly),
                         currentDirectory,
                         "directories",
                         ref failedCount,
                         failures))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryRepairDirectory(childDirectory, ref repairedCount, ref failedCount, failures))
                {
                    pendingDirectories.Push(childDirectory);
                }
            }
        }

        var detail = failedCount == 0
            ? $"Runtime data ACL repaired. Root={rootDirectory}, RepairedItems={repairedCount}, FailedItems=0."
            : $"Runtime data ACL repaired with warnings. Root={rootDirectory}, RepairedItems={repairedCount}, FailedItems={failedCount}, FirstFailures={string.Join(" || ", failures)}.";

        return new RuntimeDataAclRepairResult(
            true,
            failedCount == 0 ? "OK" : "OK_WITH_WARNINGS",
            detail);
    }

    private static bool TryRepairDirectory(
        string directoryPath,
        ref int repairedCount,
        ref int failedCount,
        List<string> failures)
    {
        try
        {
            RepairDirectoryAcl(directoryPath);
            repairedCount++;
            return true;
        }
        catch (Exception ex) when (IsRepairException(ex))
        {
            AddFailure(ref failedCount, failures, directoryPath, ex);
            return false;
        }
    }

    private static bool TryRepairFile(
        string filePath,
        ref int repairedCount,
        ref int failedCount,
        List<string> failures)
    {
        try
        {
            RepairFileAcl(filePath);
            repairedCount++;
            return true;
        }
        catch (Exception ex) when (IsRepairException(ex))
        {
            AddFailure(ref failedCount, failures, filePath, ex);
            return false;
        }
    }

    private static void RepairDirectoryAcl(string directoryPath)
    {
        var directoryInfo = new DirectoryInfo(directoryPath);
        var security = directoryInfo.GetAccessControl(AccessControlSections.Access);
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        RemoveBroadUserWriteDenies(security);
        security.SetAccessRule(CreateDirectoryUsersModifyRule());
        directoryInfo.SetAccessControl(security);
    }

    private static void RepairFileAcl(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var security = fileInfo.GetAccessControl(AccessControlSections.Access);
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        RemoveBroadUserWriteDenies(security);
        security.SetAccessRule(CreateFileUsersModifyRule());
        fileInfo.SetAccessControl(security);
    }

    private static FileSystemAccessRule CreateDirectoryUsersModifyRule()
        => new(
            BuiltinUsersSid,
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);

    private static FileSystemAccessRule CreateFileUsersModifyRule()
        => new(
            BuiltinUsersSid,
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow);

    private static void RemoveBroadUserWriteDenies(FileSystemSecurity security)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Deny
                || !IsBroadUserSid((SecurityIdentifier)rule.IdentityReference)
                || !DeniesWrite(rule.FileSystemRights))
            {
                continue;
            }

            security.RemoveAccessRuleSpecific(rule);
        }
    }

    private static bool IsBroadUserSid(SecurityIdentifier sid)
        => sid.Equals(BuiltinUsersSid)
           || sid.Equals(AuthenticatedUsersSid)
           || sid.Equals(WorldSid);

    private static bool DeniesWrite(FileSystemRights rights)
    {
        const FileSystemRights writeRights =
            FileSystemRights.Write
            | FileSystemRights.Modify
            | FileSystemRights.FullControl
            | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership;

        return (rights & writeRights) != 0;
    }

    private static IEnumerable<string> EnumerateFileSystemEntries(
        Func<IEnumerable<string>> enumerate,
        string directoryPath,
        string entryKind,
        ref int failedCount,
        List<string> failures)
    {
        try
        {
            return enumerate().ToArray();
        }
        catch (Exception ex) when (IsRepairException(ex))
        {
            AddFailure(ref failedCount, failures, $"{directoryPath} ({entryKind})", ex);
            return [];
        }
    }

    private static void AddFailure(ref int failedCount, List<string> failures, string path, Exception exception)
    {
        failedCount++;
        if (failures.Count >= MaxFailureDetails)
        {
            return;
        }

        failures.Add($"{path}: {exception.GetType().Name}: {exception.Message}");
    }

    private static bool IsRepairException(Exception exception)
        => exception is UnauthorizedAccessException
            or IOException
            or SecurityException;
}
