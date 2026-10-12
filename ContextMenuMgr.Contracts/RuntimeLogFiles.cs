using System.Collections.Concurrent;

namespace ContextMenuMgr.Contracts;

/// <summary>Explicit ownership and budgets for all persistent application log streams.</summary>
public static class RuntimeLogFiles
{
    public const int LargeSegmentBytes = 5 * 1024 * 1024;
    public const int SmallSegmentBytes = 2 * 1024 * 1024;
    private static readonly string[] LargeStreams = ["frontend-debug.log", "backend.log"];
    private static readonly string[] SmallStreams =
        ["trayhost.log", "frontend-crash.log", "service-startup.log", "backend-fallback.log", "bootstrap.log"];
    private static readonly ConcurrentDictionary<string, RollingLogFile> EmergencyWriters = new(StringComparer.OrdinalIgnoreCase);

    public static string EmergencyDirectory => Path.Combine(Path.GetTempPath(), "ContextMenuMgr", "Logs");

    public static IReadOnlyList<string> EmergencyDirectoriesForCleanup()
    {
        var directories = new List<string> { EmergencyDirectory };
        if (OperatingSystem.IsWindows())
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var systemTemp = Environment.GetEnvironmentVariable("SystemTemp", EnvironmentVariableTarget.Machine);
            var olderTemp = Environment.GetEnvironmentVariable("TMP", EnvironmentVariableTarget.Machine)
                ?? Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.Machine);
            directories.Add(Path.Combine(systemTemp ?? Path.Combine(windows, "SystemTemp"), "ContextMenuMgr", "Logs"));
            directories.Add(Path.Combine(olderTemp ?? Path.Combine(windows, "Temp"), "ContextMenuMgr", "Logs"));
        }
        return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static void RecoverEmergency(string name)
    {
        if (!SmallStreams.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            var path = Path.Combine(RuntimePaths.LogsDirectory, name);
            EmergencyWriters.GetOrAdd(path, Create).Recover();
        }
        catch { }
        try
        {
            var path = Path.Combine(EmergencyDirectory, name);
            if (File.Exists(path)) EmergencyWriters.GetOrAdd(path, Create).Recover();
        }
        catch { }
    }

    public static RollingLogFile Create(string path)
    {
        var large = LargeStreams.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
        var writer = new RollingLogFile(path, large ? LargeSegmentBytes : SmallSegmentBytes, large ? 10 : 5);
        writer.Recover();
        return writer;
    }

    /// <summary>Independent best effort, including when normal logger/DI/root initialization fails.</summary>
    public static void WriteEmergency(string name, string entry)
    {
        if (!SmallStreams.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            var path = Path.Combine(RuntimePaths.LogsDirectory, name);
            if (EmergencyWriters.GetOrAdd(path, Create).Append(entry)) return;
        }
        catch { }
        try
        {
            var path = Path.Combine(EmergencyDirectory, name);
            EmergencyWriters.GetOrAdd(path, Create).Append(entry);
        }
        catch { } // Both destinations failed; never recurse through ordinary logging.
    }

    /// <summary>Clears known active files and exact owned archive names under stream leases.</summary>
    public static async Task<IReadOnlyList<string>> ClearAsync(string directory, CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0)
                throw new IOException("The log directory is occupied by a file.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return failures; }
        catch (Exception ex) { failures.Add($"{directory}: {ex.Message}"); return failures; }
        foreach (var name in LargeStreams.Concat(SmallStreams))
        {
            var path = Path.Combine(directory, name);
            try
            {
                using var lease = await RollingLogFile.AcquireLeaseAsync(path, cancellationToken).ConfigureAwait(false);
                foreach (var archive in RollingLogFile.Archives(path))
                {
                    try { RollingLogFile.EnsureRegularFile(archive); File.Delete(archive); }
                    catch (Exception ex) { failures.Add($"{archive}: {ex.Message}"); }
                }
                if (File.Exists(path))
                {
                    using var active = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                    active.SetLength(0);
                }
            }
            catch (Exception ex) { failures.Add($"{path}: {ex.Message}"); }
        }
        return failures;
    }
}
