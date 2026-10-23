using System.Collections.Concurrent;
using System.Diagnostics;

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
    private static int _lockFailureCount;
    [ThreadStatic] private static bool _reportingLockFailure;

    public static string EmergencyDirectory => Path.Combine(Path.GetTempPath(), "ContextMenuMgr", "Logs");

    internal static bool IsOwnedStream(string path)
        => LargeStreams.Concat(SmallStreams).Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Recreates owned empty leases in Locks; never copies synchronization files.</summary>
    public static void MigrateLegacyLocks(string directory)
    {
        foreach (var name in LargeStreams.Concat(SmallStreams))
        {
            var path = Path.Combine(directory, name);
            try
            {
                if (!File.Exists(path + ".lock")) continue;
                // A busy legacy process postpones this stream. Its next new write/recovery
                // retries naturally; no process census or brief open proves retirement safe.
                using var lease = RollingLogFile.TryAcquireLease(path, out var failure);
                if (lease is null) throw new IOException("Legacy lock migration deferred: stream is busy or inaccessible.", failure);
            }
            catch (Exception ex) { ReportLockFailure(path, ex); }
        }
    }

    /// <summary>Portable copy-only upgrade: exact owned logs, no locks or recursive traversal.</summary>
    public static void CopyLegacyLogsIfMissing(string sourceDirectory, string destinationDirectory)
    {
        if (string.Equals(Path.GetFullPath(sourceDirectory), Path.GetFullPath(destinationDirectory), StringComparison.OrdinalIgnoreCase)) return;
        foreach (var name in LargeStreams.Concat(SmallStreams))
        {
            var source = Path.Combine(sourceDirectory, name);
            var destination = Path.Combine(destinationDirectory, name);
            try
            {
                if (!Directory.Exists(sourceDirectory)) return;
                RollingLogFile.EnsureDirectory(sourceDirectory);
                if (!File.Exists(source) && !RollingLogFile.Archives(source).Any()) continue;
                // Stable order also covers two simultaneous upgrades with reversed roots.
                var ordered = new[] { source, destination }.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                using var first = RollingLogFile.TryAcquireLease(ordered[0], out var firstFailure);
                if (first is null) throw new IOException("Legacy log copy deferred: first stream is busy or inaccessible.", firstFailure);
                using var second = RollingLogFile.TryAcquireLease(ordered[1], out var secondFailure);
                if (second is null) throw new IOException("Legacy log copy deferred: second stream is busy or inaccessible.", secondFailure);
                foreach (var file in RollingLogFile.Archives(source).Prepend(source))
                {
                    if (!File.Exists(file)) continue;
                    RollingLogFile.EnsureRegularFile(file);
                    var target = Path.Combine(destinationDirectory, Path.GetFileName(file));
                    RollingLogFile.EnsureRegularFile(target);
                    if (!File.Exists(target)) File.Copy(file, target, overwrite: false);
                }
            }
            catch (Exception ex) { ReportLockFailure(source, ex); }
        }
    }

    internal static void ReportLockFailure(string path, Exception exception)
    {
        // At most eight diagnostics per process; no per-path cache, normal logger or recursion.
        if (_reportingLockFailure || Interlocked.Increment(ref _lockFailureCount) > 8) return;
        _reportingLockFailure = true;
        try
        {
            var detail = $"LogLockMaintenance: Path={path}, {exception.GetType().Name}: {exception.Message}";
            if (exception.InnerException is not null)
            {
                var cause = exception.GetBaseException();
                detail += $" Cause={cause.GetType().Name}: {cause.Message}";
            }
            if (detail.Length > 1024) detail = detail[..1024];
            Trace.TraceWarning(detail);
            // Already handling a TEMP failure: do not reenter its held lease.
            if (Path.GetFullPath(path).StartsWith(Path.GetFullPath(EmergencyDirectory) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) return;
            var fallback = Path.Combine(EmergencyDirectory, "backend-fallback.log");
            EmergencyWriters.GetOrAdd(fallback, Create).Append($"[{DateTimeOffset.Now:O}] {detail}{Environment.NewLine}");
        }
        catch { }
        finally { _reportingLockFailure = false; }
    }

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
        MigrateLegacyLocks(Path.GetDirectoryName(Path.GetFullPath(path))!);
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
            RollingLogFile.EnsureDirectory(directory);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return failures; }
        catch (Exception ex) { failures.Add($"{directory}: {ex.Message}"); return failures; }
        foreach (var name in LargeStreams.Concat(SmallStreams))
        {
            var path = Path.Combine(directory, name);
            try
            {
                // Do not create coordination files for absent streams during maintenance.
                if (!File.Exists(path) && !RollingLogFile.Archives(path).Any()) continue;
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
