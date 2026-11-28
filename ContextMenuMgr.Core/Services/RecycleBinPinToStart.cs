using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ContextMenuMgr.Backend.Services;

internal interface IRecycleBinPinToStartRegistry
{
    string? ReadMachineHandler();
    PersistedRecycleBinRegistryKeySnapshot Read(string path);
    void Write(string path, PersistedRecycleBinRegistryKeySnapshot expected, PersistedRecycleBinRegistryKeySnapshot next);
}

internal sealed class WindowsRecycleBinPinToStartRegistry : IRecycleBinPinToStartRegistry
{
    private const string MachinePath = @"SOFTWARE\Classes\Folder\shellex\ContextMenuHandlers\PintoStartScreen";

    public string? ReadMachineHandler()
    {
        using var key = Registry.LocalMachine.OpenSubKey(MachinePath, writable: false);
        if (key is null || !key.GetValueNames().Contains(string.Empty, StringComparer.OrdinalIgnoreCase)
            || key.GetValueKind(string.Empty) != RegistryValueKind.String)
            return null;
        return RecycleBinPinToStartMutation.ValidateMachineHandler(key.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
    }

    public PersistedRecycleBinRegistryKeySnapshot Read(string path)
    {
        using var key = ContextMenuRegistryCatalog.OpenRegistryKey(path, writable: false);
        if (key is null) return new();
        var names = key.GetValueNames();
        return new PersistedRecycleBinRegistryKeySnapshot
        {
            Existed = true,
            LastWriteUtc = ContextMenuRegistryCatalog.TryGetRegistryWriteTimeUtc(path, out var writeTime) ? writeTime : null,
            DefaultValue = ShellVerbVisibility.CaptureValue(key, string.Empty),
            OtherValues = names.Where(static name => name.Length > 0)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => ShellVerbVisibility.CaptureValue(key, name)).ToList(),
            SubKeyNames = key.GetSubKeyNames().OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    public void Write(string path, PersistedRecycleBinRegistryKeySnapshot expected, PersistedRecycleBinRegistryKeySnapshot next)
    {
        if (!RecycleBinPinToStartMutation.Same(Read(path), expected))
            throw new InvalidOperationException("The frontend user's Pin to Start registry key changed before the write.");
        var prefix = @"HKEY_USERS\";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Pin to Start mutations require HKEY_USERS and a frontend SID.");
        var relative = path[prefix.Length..];
        if (next.Existed)
        {
            if (expected.Existed)
                throw new InvalidOperationException("An existing user key cannot be adopted by this transaction.");
            var status = RegCreateKeyEx(Registry.Users.Handle.DangerousGetHandle(), relative,
                0, null, 0, 0x2001F, IntPtr.Zero, out var handle, out var disposition);
            if (status != 0) throw new Win32Exception(status, $"Unable to create {path}.");
            if (disposition != 1)
            {
                handle.Dispose();
                throw new InvalidOperationException("The frontend-user Pin to Start key was created by another process.");
            }
            try
            {
                using var key = RegistryKey.FromHandle(handle);
                // A new Folder shadow needs no value write at all. For Directory,
                // detect a concurrent registration before setting its default.
                if (next.DefaultValue.Existed)
                {
                    if (!RecycleBinPinToStartMutation.Same(Read(path), RecycleBinPinToStartMutation.ManagedFolder()))
                        throw new InvalidOperationException("The newly created Pin to Start key changed before registration.");
                    ShellVerbVisibility.RestoreValue(key, next.DefaultValue);
                }
            }
            catch
            {
                // If the provider failed before writing the default, remove only
                // the still-empty leaf that this RegCreateKeyEx call created.
                if (RecycleBinPinToStartMutation.Same(Read(path), RecycleBinPinToStartMutation.ManagedFolder()))
                    Registry.Users.DeleteSubKey(relative, throwOnMissingSubKey: false);
                throw;
            }
        }
        else
        {
            // The compare above ensures an empty leaf. Never remove a key with
            // additional values or children owned by another application.
            Registry.Users.DeleteSubKey(relative, throwOnMissingSubKey: false);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyEx(IntPtr root, string subKey, int reserved, string? keyClass,
        uint options, int desiredAccess, IntPtr securityAttributes, out SafeRegistryHandle result,
        out uint disposition);
}

internal sealed class RecycleBinPinToStartMutation(IRecycleBinPinToStartRegistry registry)
{
    internal const string Id = "special:recyclebin:pintostart";
    internal const string SourceRoot = "special:recyclebin:pintostart";
    internal const string MachineDisplayPath = @"HKLM\SOFTWARE\Classes\Folder\shellex\ContextMenuHandlers\PintoStartScreen";

    internal static string FolderPath(string sid) => $@"HKEY_USERS\{sid}\Software\Classes\Folder\shellex\ContextMenuHandlers\PintoStartScreen";
    internal static string DirectoryPath(string sid) => $@"HKEY_USERS\{sid}\Software\Classes\Directory\shellex\ContextMenuHandlers\PintoStartScreen";

    internal static string? ValidateMachineHandler(string? value)
        => value is not null && Guid.TryParseExact(value, "B", out var guid) && guid != Guid.Empty
            ? guid.ToString("B").ToUpperInvariant() : null;

    internal string? GetMachineHandler() => registry.ReadMachineHandler();

    internal bool IsManagedDisabled(string sid, string handler, PersistedRecycleBinPinToStartProvenance? provenance)
        => ValidProvenance(sid, handler, provenance)
           && Same(registry.Read(FolderPath(sid)), provenance!.FolderManaged)
           && Same(registry.Read(DirectoryPath(sid)), provenance.DirectoryManaged);

    internal static bool ValidProvenance(string sid, string handler, PersistedRecycleBinPinToStartProvenance? provenance)
        => provenance is { SchemaVersion: 1 }
           && string.Equals(provenance.UserSid, sid, StringComparison.OrdinalIgnoreCase)
           && string.Equals(provenance.MachineHandlerClsid, handler, StringComparison.OrdinalIgnoreCase)
           && provenance.FolderBefore is { Existed: false }
           && provenance.DirectoryBefore is { Existed: false }
           && provenance.FolderManaged is { } folderManaged
           && provenance.DirectoryManaged is { } directoryManaged
           && Same(folderManaged, ManagedFolder())
           && Same(directoryManaged, ManagedDirectory(handler));

    internal static PersistedRecycleBinPinToStartProvenance CaptureProvenance(
        string sid, string handler, PersistedRecycleBinRegistryKeySnapshot folder,
        PersistedRecycleBinRegistryKeySnapshot directory) => new()
    {
        UserSid = sid, MachineHandlerClsid = handler, FolderBefore = folder, DirectoryBefore = directory
    };

    internal async Task<PersistedRecycleBinPinToStartProvenance?> ExecuteAsync(
        bool enable, string sid, string handler, PersistedRecycleBinPinToStartProvenance? provenance,
        Func<PersistedRecycleBinPinToStartProvenance?, Task> commit)
    {
        if (string.IsNullOrWhiteSpace(sid) || sid.Contains('\\') || sid.Contains('/'))
            throw new InvalidOperationException("The frontend user SID is missing or invalid.");
        if (ValidateMachineHandler(handler) is null)
            throw new InvalidOperationException("The machine PintoStartScreen handler is invalid.");

        var folderPath = FolderPath(sid);
        var directoryPath = DirectoryPath(sid);
        var folder = registry.Read(folderPath);
        var directory = registry.Read(directoryPath);
        var managedFolder = ManagedFolder();
        var managedDirectory = ManagedDirectory(handler);
        var owned = ValidProvenance(sid, handler, provenance);

        if (provenance is not null && !owned)
            throw new InvalidOperationException("Pin to Start provenance belongs to another user or machine registration.");
        if (owned && (!Same(folder, provenance!.FolderManaged) || !Same(directory, provenance.DirectoryManaged)))
            throw new InvalidOperationException("The managed Pin to Start keys changed externally; they were not overwritten.");

        if (!enable && owned) return provenance; // Repeated disable retains the original generation.
        if (enable && !owned && !folder.Existed && !directory.Existed) return null;
        if (!owned && (folder.Existed || directory.Existed))
            throw new InvalidOperationException("A frontend-user Pin to Start override already exists; ContextMenuMgrPlus does not own it.");

        var beforeFolder = folder;
        var beforeDirectory = directory;
        var afterFolder = enable ? provenance!.FolderBefore : managedFolder;
        var afterDirectory = enable ? provenance!.DirectoryBefore : managedDirectory;
        var nextProvenance = enable ? null : CaptureProvenance(sid, handler, folder, directory);

        // Mark each step before invoking the provider: a provider may throw after
        // committing. Rollback still compares physical state before restoring.
        var folderAttempted = false;
        var directoryAttempted = false;
        var writtenFolder = afterFolder;
        var writtenDirectory = afterDirectory;
        try
        {
            folderAttempted = true;
            registry.Write(folderPath, beforeFolder, afterFolder);
            writtenFolder = registry.Read(folderPath);
            if (!Same(writtenFolder, afterFolder))
                throw new InvalidOperationException("Pin to Start Folder readback failed.");
            directoryAttempted = true;
            registry.Write(directoryPath, beforeDirectory, afterDirectory);
            writtenDirectory = registry.Read(directoryPath);
            if (!Same(registry.Read(folderPath), writtenFolder)
                || !Same(writtenDirectory, afterDirectory))
                throw new InvalidOperationException("Pin to Start physical readback failed.");
            if (nextProvenance is not null)
            {
                nextProvenance.FolderManaged = writtenFolder;
                nextProvenance.DirectoryManaged = writtenDirectory;
            }
            await commit(nextProvenance);
            return nextProvenance;
        }
        catch (Exception error)
        {
            var conflict = false;
            var rollbackError = false;
            foreach (var step in new[]
            {
                (directoryAttempted, directoryPath, beforeDirectory, writtenDirectory),
                (folderAttempted, folderPath, beforeFolder, writtenFolder)
            })
            {
                if (!step.Item1) continue;
                try
                {
                    var current = registry.Read(step.Item2);
                    if (Same(current, step.Item3)) continue;
                    if (!Same(current, step.Item4)) { conflict = true; continue; }
                    registry.Write(step.Item2, step.Item4, step.Item3);
                    if (!Same(registry.Read(step.Item2), step.Item3)) rollbackError = true;
                }
                catch { rollbackError = true; }
            }
            throw new RecycleBinPinToStartMutationException(error, conflict || rollbackError);
        }
    }

    internal static PersistedRecycleBinRegistryKeySnapshot ManagedFolder() => new() { Existed = true };
    internal static PersistedRecycleBinRegistryKeySnapshot ManagedDirectory(string handler) => new()
    {
        Existed = true,
        DefaultValue = new PersistedRegistryValueSnapshot
        {
            Existed = true, Name = string.Empty, Kind = (int)RegistryValueKind.String, StringValue = handler
        }
    };

    internal static bool Same(PersistedRecycleBinRegistryKeySnapshot left, PersistedRecycleBinRegistryKeySnapshot right)
        => left.Existed == right.Existed
           && (!left.Existed || (SameValue(left.DefaultValue, right.DefaultValue)
               && (left.LastWriteUtc is null || right.LastWriteUtc is null || left.LastWriteUtc == right.LastWriteUtc)
               && left.OtherValues.Count == right.OtherValues.Count
               && left.OtherValues.Zip(right.OtherValues).All(pair => SameValue(pair.First, pair.Second))
               && left.SubKeyNames.SequenceEqual(right.SubKeyNames, StringComparer.OrdinalIgnoreCase)));

    private static bool SameValue(PersistedRegistryValueSnapshot left, PersistedRegistryValueSnapshot right)
        => left.Existed == right.Existed
           && (!left.Existed || (string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
               && left.Kind == right.Kind && left.StringValue == right.StringValue
               && left.IntegerValue == right.IntegerValue && left.BinaryBase64 == right.BinaryBase64
               && (left.StringArrayValue ?? []).SequenceEqual(right.StringArrayValue ?? [])));
}

internal sealed class RecycleBinPinToStartMutationException(Exception cause, bool rollbackConflict)
    : Exception(cause.Message, cause)
{
    public bool RollbackConflict { get; } = rollbackConflict;
}
