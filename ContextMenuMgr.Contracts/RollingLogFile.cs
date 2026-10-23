using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ContextMenuMgr.Contracts;

/// <summary>Small shared UTF-8 writer. No open data handles, queues, or logging callbacks.</summary>
public sealed class RollingLogFile(string path, int maximumBytes, int archiveCount)
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string _path = Validate(path, maximumBytes, archiveCount);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private string? _sessionHeader;
    private bool _sessionReady;
    private bool _finalized;
    private long _retryAfter;

    public void Recover()
    {
        if (!_gate.Wait(250)) return;
        try
        {
            using var lease = AcquireLease(_path);
            if (lease is not null) Initialize();
        }
        catch { } // Startup must proceed; subsequent writes retry recovery.
        finally { _gate.Release(); }
    }

    public bool Append(string entry)
    {
        if (!_gate.Wait(250)) return false;
        try
        {
            using var lease = AcquireLease(_path);
            return lease is not null && AppendCore(entry);
        }
        catch { return false; }
        finally { _gate.Release(); }
    }

    public async Task<bool> AppendAsync(string entry, CancellationToken cancellationToken = default)
    {
        var acquired = false;
        try
        {
            acquired = await _gate.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            if (!acquired) return false;
            using var lease = await AcquireLeaseAsync(_path, cancellationToken).ConfigureAwait(false);
            if (!Prepare(entry, out var bytes)) return false;
            using var file = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous);
            // Once started, finish the entry even if the request is canceled.
            await file.WriteAsync(bytes).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
        finally { if (acquired) _gate.Release(); }
    }

    /// <summary>Only the primary Frontend calls this, after acquiring its application mutex.</summary>
    public void BeginSession(string reason)
    {
        if (!_gate.Wait(250)) return;
        try
        {
            if (_sessionHeader is not null) return;
            if (reason.Length > 64) reason = reason[..64];
            _sessionHeader = $"SessionStarted: Time={DateTimeOffset.Now:O}, PID={Environment.ProcessId}, SessionId={Guid.NewGuid():N}, Reason={reason}{Environment.NewLine}";
            using var lease = AcquireLease(_path);
            if (lease is not null) PrepareSession();
        }
        catch { }
        finally { _gate.Release(); }
    }

    public void EndSession(bool normalShutdown)
    {
        if (!_gate.Wait(250)) return;
        try
        {
            if (_sessionHeader is null || _finalized) return;
            using var lease = AcquireLease(_path);
            if (lease is not null && AppendCore($"SessionEnded: Time={DateTimeOffset.Now:O}, NormalShutdown={normalShutdown}{Environment.NewLine}")
                && normalShutdown)
                Rotate("shutdown");
            _finalized = true;
        }
        catch { _finalized = true; }
        finally { _gate.Release(); }
    }

    private bool AppendCore(string entry)
    {
        if (!Prepare(entry, out var bytes)) return false;
        using var file = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        file.Write(bytes);
        return true;
    }

    private bool Prepare(string entry, out byte[] bytes)
    {
        bytes = [];
        if (_finalized) return false;
        if (Environment.TickCount64 < _retryAfter)
        {
            // Clear All Logs truncates the active file under the same lease. Observe that
            // across processes and resume immediately, rather than waiting out a failed roll.
            if (!File.Exists(_path) || new FileInfo(_path).Length != 0) return false;
            _retryAfter = 0;
        }
        try
        {
            Initialize();
            PrepareSession();
            // Keep normal entries intact. Exceptional giant entries retain both ends, including
            // the exception/stack tail, with an explicit omission marker inside the size budget.
            bytes = BoundEntry(entry, maximumBytes - (_sessionHeader is null ? 0 : Utf8.GetByteCount(_sessionHeader)));
            var length = File.Exists(_path) ? new FileInfo(_path).Length : 0;
            if (length + bytes.Length > maximumBytes)
            {
                Rotate("size");
                WriteSessionHeader();
            }
            else if (length == 0) WriteSessionHeader();
            return true;
        }
        catch
        {
            // Do not grow files or create more archives when pruning/rotation is blocked.
            _retryAfter = Environment.TickCount64 + 30_000;
            return false;
        }
    }

    private void Initialize()
    {
        if (_initialized) return;
        // Scans occur only at initialization and rotation, never on an ordinary append.
        Prune(archiveCount);
        if (File.Exists(_path) && new FileInfo(_path).Length > maximumBytes) Rotate("legacy-tail");
        _initialized = true;
    }

    private void PrepareSession()
    {
        if (_sessionHeader is null || _sessionReady) return;
        Initialize();
        if (File.Exists(_path) && new FileInfo(_path).Length > 0) Rotate("recovered");
        WriteSessionHeader();
        _sessionReady = true;
    }

    private void WriteSessionHeader()
    {
        if (_sessionHeader is not null)
        {
            using var file = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            file.Write(Utf8.GetBytes(_sessionHeader));
        }
    }

    private void Rotate(string reason)
    {
        if (!File.Exists(_path)) return;
        Prune(archiveCount - 1); // Refuse rotation if retention cannot be enforced.
        var archive = Path.Combine(Path.GetDirectoryName(_path)!,
            $"{Path.GetFileNameWithoutExtension(_path)}.archive-{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{reason}-{Guid.NewGuid():N}.log");
        if (new FileInfo(_path).Length <= maximumBytes)
        {
            File.Move(_path, archive); // Never overwrite, even on timestamp collisions.
            return;
        }

        // Upgrade policy: retain only a bounded recent tail, not a permanent giant archive.
        // Read at most one segment; the original is removed only after the archive is flushed.
        var createdArchive = false;
        try
        {
            using (var source = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var target = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                createdArchive = true;
                var marker = Utf8.GetBytes($"LegacyOversizedLog: OriginalBytes={source.Length}, RetainedRecentTail=true{Environment.NewLine}");
                var tail = new byte[maximumBytes - marker.Length];
                source.Seek(-tail.Length, SeekOrigin.End);
                source.ReadExactly(tail);
                var start = Array.IndexOf(tail, (byte)'\n') + 1;
                if (start == 0 || start == tail.Length)
                {
                    start = 0; // A giant single line still has useful recent tail evidence.
                    while (start < tail.Length && (tail[start] & 0xC0) == 0x80) start++;
                }
                target.Write(marker);
                target.Write(tail.AsSpan(start));
                target.Flush(flushToDisk: true);
            }
            File.Delete(_path);
        }
        catch
        {
            if (createdArchive)
                try { File.Delete(archive); } catch { }
            throw;
        }
    }

    private void Prune(int keep)
    {
        foreach (var file in Archives(_path).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(keep))
        {
            EnsureRegularFile(file);
            File.Delete(file);
        }
    }

    private static byte[] BoundEntry(string entry, int limit)
    {
        if (Utf8.GetByteCount(entry) <= limit) return Utf8.GetBytes(entry);
        const string marker = "\n[LogEntryTruncated: middle omitted to enforce segment limit]\n";
        var budget = (limit - Utf8.GetByteCount(marker)) / 2;
        // UTF-16 chars use at most three UTF-8 bytes each (a surrogate pair uses four).
        var count = Math.Max(0, budget / 3 - 1);
        var head = entry.AsSpan(0, Math.Min(count, entry.Length));
        var tail = entry.AsSpan(Math.Max(0, entry.Length - count));
        if (head.Length > 0 && char.IsHighSurrogate(head[^1])) head = head[..^1];
        if (tail.Length > 0 && char.IsLowSurrogate(tail[0])) tail = tail[1..];
        return Utf8.GetBytes(string.Concat(head, marker, tail));
    }

    internal static IEnumerable<string> Archives(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) return [];
        var stem = Path.GetFileNameWithoutExtension(path);
        var pattern = $"^{Regex.Escape(stem)}\\.archive-[0-9]{{8}}T[0-9]{{13}}Z-(size|shutdown|recovered|legacy-tail)-[0-9a-f]{{32}}\\.log$";
        return Directory.EnumerateFiles(directory, stem + ".archive-*.log")
            .Where(file => Regex.IsMatch(Path.GetFileName(file), pattern, RegexOptions.CultureInvariant));
    }

    private static string Validate(string path, int maximumBytes, int archiveCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(archiveCount, 1);
        return Path.GetFullPath(path);
    }

    internal static IDisposable? TryAcquireLease(string path, out Exception? failure)
    {
        failure = null;
        FileStream? current = null;
        FileStream? legacy = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var locks = Path.Combine(Path.GetDirectoryName(directory)!, "Locks");
            EnsureDirectory(directory);
            EnsureDirectory(locks);
            EnsureRegularFile(path);
            var lockPath = Path.Combine(locks, Path.GetFileName(path) + ".lock");
            EnsureRegularFile(lockPath);
            current = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            if (current.Length != 0) throw new IOException("The lock destination contains unrelated data.");

            // Always take both leases, even on a clean install: a sleeping old writer can
            // recreate its path at any time. New -> legacy is the only acquisition order.
            EnsureRegularFile(path + ".lock");
            legacy = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.Delete);
            var recognized = RuntimeLogFiles.IsOwnedStream(path);
            return new LogLease(current, legacy, path + ".lock", recognized, legacy.Length == 0);
        }
        catch (Exception ex)
        {
            legacy?.Dispose();
            current?.Dispose();
            failure = ex;
            return null;
        }
    }

    internal static async Task<IDisposable> AcquireLeaseAsync(string path, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        Exception? failure;
        do
        {
            if (TryAcquireLease(path, out failure) is { } lease) return lease;
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        } while (timer.ElapsedMilliseconds < 1000);
        var unavailable = new IOException("Log stream is locked or inaccessible.", failure);
        RuntimeLogFiles.ReportLockFailure(path, unavailable);
        throw unavailable;
    }

    internal static void EnsureRegularFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Refusing to modify a log path that is a directory or reparse point.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
    }

    internal static void EnsureDirectory(string path)
    {
        // Do not follow a redirected Logs/Locks directory (or runtime root) into user files.
        foreach (var candidate in new[] { Path.GetDirectoryName(path), path })
        {
            if (candidate is not null && Directory.Exists(candidate)
                && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to use a log directory that is a reparse point.");
        }
        Directory.CreateDirectory(path);
    }

    private sealed class LogLease(FileStream current, FileStream legacy, string legacyPath, bool recognized, bool empty) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Exception? failure = null;
            try
            {
                // This is retirement AFTER the protected operation, not an exclusive-open
                // liveness probe. Windows keeps the file delete-pending until this handle
                // closes. Every future new operation still takes the legacy lease, so old
                // processes between writes remain synchronized when they recreate it.
                if (recognized && empty && OperatingSystem.IsWindows()) File.Delete(legacyPath);
                else if (recognized && !empty) failure = new IOException("Nonempty legacy lock preserved; ownership is uncertain.");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { legacy.Dispose(); }
                finally { current.Dispose(); }
            }
            if (failure is not null) RuntimeLogFiles.ReportLockFailure(legacyPath, failure);
        }
    }

    private static IDisposable? AcquireLease(string path)
    {
        var timer = Stopwatch.StartNew();
        Exception? failure;
        do
        {
            if (TryAcquireLease(path, out failure) is { } lease) return lease;
            Thread.Sleep(10);
        } while (timer.ElapsedMilliseconds < 250);
        RuntimeLogFiles.ReportLockFailure(path, new IOException("Log stream is locked or inaccessible.", failure));
        return null;
    }
}
