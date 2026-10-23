using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class RollingLogFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-log-tests-" + Guid.NewGuid().ToString("N"));
    private string _directory => Path.Combine(_root, "Logs");
    private string Locks => Path.Combine(_root, "Locks");
    private string Active => Path.Combine(_directory, "backend.log");

    public RollingLogFileTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void StartupMigration_IsIdempotentAndPreservesLogsArchivesAndUnknownFiles()
    {
        File.WriteAllText(Active, "existing diagnostic evidence");
        var archive = Path.Combine(_directory, $"backend.archive-20261023T0102030000000Z-size-{Guid.NewGuid():N}.log");
        File.WriteAllText(archive, "archive evidence");
        File.WriteAllText(Active + ".lock", "");
        File.WriteAllText(Path.Combine(_directory, "trayhost.log.lock"), "");
        var unknown = Path.Combine(_directory, "personal.log.lock");
        File.WriteAllText(unknown, "user data");
        var nonempty = Path.Combine(_directory, "bootstrap.log.lock");
        File.WriteAllText(nonempty, "not an owned empty lease");

        for (var i = 0; i < 3; i++)
        {
            RuntimeLogFiles.Create(Active).Recover();
            Assert.False(File.Exists(Active + ".lock"));
            Assert.False(File.Exists(Path.Combine(_directory, "trayhost.log.lock")));
            Assert.Equal(0, new FileInfo(Path.Combine(Locks, "backend.log.lock")).Length);
            Assert.Equal(0, new FileInfo(Path.Combine(Locks, "trayhost.log.lock")).Length);
            Assert.Equal("existing diagnostic evidence", File.ReadAllText(Active));
            Assert.Equal("archive evidence", File.ReadAllText(archive));
            Assert.Equal("user data", File.ReadAllText(unknown));
            Assert.Equal("not an owned empty lease", File.ReadAllText(nonempty));
        }
    }

    [Fact]
    public async Task DestinationConflictsAndReadOnlyLocks_DeferWithoutTouchingEvidenceAndRetrySafely()
    {
        File.WriteAllText(Active, "evidence");
        File.WriteAllText(Active + ".lock", "");
        Directory.CreateDirectory(Locks);
        var destination = Path.Combine(Locks, "backend.log.lock");
        File.WriteAllText(destination, "unrelated destination");
        var writer = new RollingLogFile(Active, 1024, 3);
        Assert.False(writer.Append("must not append"));
        Assert.Equal("evidence", File.ReadAllText(Active));
        Assert.Equal("unrelated destination", File.ReadAllText(destination));
        Assert.True(File.Exists(Active + ".lock"));
        Assert.NotEmpty(await RuntimeLogFiles.ClearAsync(_directory));
        File.Delete(destination);

        Directory.CreateDirectory(destination);
        Assert.False(writer.Append("directory conflict"));
        Assert.True(Directory.Exists(destination));
        Assert.Equal("evidence", File.ReadAllText(Active));
        Directory.Delete(destination);

        File.WriteAllText(destination, "");
        File.SetAttributes(destination, FileAttributes.ReadOnly);
        try
        {
            RuntimeLogFiles.MigrateLegacyLocks(_directory);
            Assert.False(writer.Append("denied"));
            Assert.Equal("evidence", File.ReadAllText(Active));
            Assert.True(File.Exists(Active + ".lock"));
        }
        finally { File.SetAttributes(destination, FileAttributes.Normal); }

        Assert.True(writer.Append("resumed"));
        Assert.Equal("evidenceresumed", File.ReadAllText(Active));
        Assert.False(File.Exists(Active + ".lock"));
        var existingLocks = Directory.GetFiles(Locks);
        Assert.Empty(await RuntimeLogFiles.ClearAsync(_directory));
        Assert.Equal(existingLocks, Directory.GetFiles(Locks));
        Assert.Empty(Directory.GetFiles(_directory, "*.lock"));
        Assert.True(writer.Append("after clear"));
        Assert.Equal("after clear", File.ReadAllText(Active));
    }

    [Fact]
    public void LegacyCopy_IsOwnedOnlyCopyOnlyAndNeverReimportsLocksOrOverwrites()
    {
        var source = Path.Combine(_root, "ProgramData", "Logs");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "backend.log"), "source backend");
        File.WriteAllText(Active, "destination backend");
        var archive = $"backend.archive-20261023T0102030000000Z-size-{Guid.NewGuid():N}.log";
        File.WriteAllText(Path.Combine(source, archive), "source archive");
        File.WriteAllText(Path.Combine(source, "trayhost.log"), "source tray");
        File.WriteAllText(Path.Combine(source, "trayhost.log.lock"), "");
        File.WriteAllText(Path.Combine(source, "personal.log"), "unrelated");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "nested", "backend.log"), "unrelated nested file");
        for (var i = 0; i < 2; i++) RuntimeLogFiles.CopyLegacyLogsIfMissing(source, _directory);
        Assert.Equal("destination backend", File.ReadAllText(Active));
        Assert.Equal("source backend", File.ReadAllText(Path.Combine(source, "backend.log")));
        Assert.Equal("source archive", File.ReadAllText(Path.Combine(_directory, archive)));
        Assert.Equal("source tray", File.ReadAllText(Path.Combine(_directory, "trayhost.log")));
        Assert.False(File.Exists(Path.Combine(_directory, "personal.log")));
        Assert.False(Directory.Exists(Path.Combine(_directory, "nested")));
        Assert.Empty(Directory.GetFiles(_directory, "*.lock"));
    }

    [Fact]
    public async Task MixedVersionWriter_RecreatesLegacyLeaseAfterMigration_AndCrashDoesNotStrandIt()
    {
        var writer = RuntimeLogFiles.Create(Active);
        Assert.True(writer.Append("new\n"));
        Assert.Empty(Directory.GetFiles(_directory, "*.lock"));
        var ready = Path.Combine(_root, "ready");
        using var old = StartLegacyProcess("""
            $lease = [IO.File]::Open($env:LOG_TEST_PATH + '.lock', 'OpenOrCreate', 'Write', 'None')
            try {
                [IO.File]::WriteAllText($env:LOG_TEST_READY, 'ready')
                Start-Sleep -Seconds 30
            } finally { $lease.Dispose() }
            """, ready);
        try
        {
            await WaitForFileAsync(ready, old);
            RuntimeLogFiles.MigrateLegacyLocks(_directory);
            Assert.True(File.Exists(Active + ".lock"));
            Assert.False(writer.Append("must not bypass old writer"));
            Assert.NotEmpty(await RuntimeLogFiles.ClearAsync(_directory));
            Assert.Equal("new\n", File.ReadAllText(Active));
        }
        finally
        {
            if (!old.HasExited) old.Kill(entireProcessTree: true);
            await old.WaitForExitAsync();
        }
        RuntimeLogFiles.MigrateLegacyLocks(_directory);
        Assert.False(File.Exists(Active + ".lock"));
        Assert.True(writer.Append("after crash\n"));
        Assert.Equal("new\nafter crash\n", File.ReadAllText(Active));
    }

    [Fact]
    public async Task SeparateOldAndNewProcesses_AppendWholeEntriesWhileLegacyPathIsRetiredAndRecreated()
    {
        var ready = Path.Combine(_root, "ready");
        using var old = StartLegacyProcess("""
            [IO.File]::WriteAllText($env:LOG_TEST_READY, 'ready')
            for ($i = 0; $i -lt 150; $i++) {
                $timer = [Diagnostics.Stopwatch]::StartNew()
                $lease = $null
                while ($null -eq $lease -and $timer.ElapsedMilliseconds -lt 5000) {
                    try { $lease = [IO.File]::Open($env:LOG_TEST_PATH + '.lock', 'OpenOrCreate', 'Write', 'None') }
                    catch [IO.IOException] { Start-Sleep -Milliseconds 1 }
                }
                if ($null -eq $lease) { throw 'old lease timeout' }
                try { [IO.File]::AppendAllText($env:LOG_TEST_PATH, "old-$i`n") }
                finally { $lease.Dispose() }
            }
            """, ready);
        try
        {
            await WaitForFileAsync(ready, old);
            await Task.WhenAll(Enumerable.Range(0, 3).Select(async index =>
            {
                var writer = RuntimeLogFiles.Create(Active);
                for (var i = 0; i < 100; i++) Assert.True(await writer.AppendAsync($"new-{index}-{i}\n"));
            }));
            await old.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, old.ExitCode);
            var lines = File.ReadAllLines(Active);
            Assert.Equal(450, lines.Length);
            Assert.Equal(450, lines.Distinct().Count());
            Assert.Equal(150, lines.Count(line => line.StartsWith("old-", StringComparison.Ordinal)));
            RuntimeLogFiles.MigrateLegacyLocks(_directory);
            Assert.Empty(Directory.GetFiles(_directory, "*.lock"));
        }
        finally
        {
            if (!old.HasExited) old.Kill(entireProcessTree: true);
            await old.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task ClearWithWriters_PreservesLeasesAndUnrelatedFilesAndWritersResume()
    {
        var unknown = Path.Combine(_directory, "personal.log");
        File.WriteAllText(unknown, "user evidence");
        var writers = Enumerable.Range(0, 3).Select(_ => new RollingLogFile(Active, 1024, 3)).ToArray();
        var writing = Task.WhenAll(writers.Select(async writer =>
        {
            for (var i = 0; i < 150; i++) Assert.True(await writer.AppendAsync("whole entry\n"));
        }));
        for (var i = 0; i < 5; i++) Assert.Empty(await RuntimeLogFiles.ClearAsync(_directory));
        await writing;
        Assert.True(writers[0].Append("resumed\n"));
        Assert.EndsWith("resumed\n", File.ReadAllText(Active));
        Assert.True(File.Exists(Path.Combine(Locks, "backend.log.lock")));
        Assert.Empty(Directory.GetFiles(_directory, "*.lock"));
        Assert.Equal("user evidence", File.ReadAllText(unknown));
        Assert.All(Directory.GetFiles(_directory, "backend*.log"), file =>
            Assert.DoesNotContain(File.ReadAllLines(file), line => line is not ("whole entry" or "resumed")));
    }

    [Fact]
    public void DeniedDirectoryAndAclRepair_DoNotRequireLoggingElevation()
    {
        Directory.CreateDirectory(Locks);
        var directoryInfo = new DirectoryInfo(Locks);
        var original = directoryInfo.GetAccessControl();
        var denied = directoryInfo.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles, AccessControlType.Deny));
        directoryInfo.SetAccessControl(denied);
        try
        {
            Assert.False(new RollingLogFile(Active, 1024, 3).Append("denied"));
            Assert.False(File.Exists(Active));
        }
        finally
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            directoryInfo.SetAccessControl(restored);
        }
        var result = RuntimeDataAclRepairService.RepairRuntimeDataDirectory(_root);
        Assert.True(result.Success, result.Detail);
        var rules = directoryInfo.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        Assert.Contains(rules.Cast<FileSystemAccessRule>(), rule => rule.AccessControlType == AccessControlType.Allow
            && rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null))
            && (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
        Assert.True(new RollingLogFile(Active, 1024, 3).Append("recovered"));
    }

    [Fact]
    public async Task RoutineRequestFiltering_DoesNotHideFailuresMutationsOrInternalErrors()
    {
        var logger = new FileLogger(Active);
        logger.Configure(RuntimeLogLevel.Warning);
        for (var i = 0; i < 100; i++)
            await logger.LogRequestOperationAsync(PipeCommand.GetWpsOfficePendingApprovals, "routine success");
        Assert.False(File.Exists(Active));
        logger.Configure(RuntimeLogLevel.Error);
        await logger.LogRequestOperationAsync(PipeCommand.GetSnapshot, "failed request CorrelationId=failed", failed: true);
        await logger.LogRequestOperationAsync(PipeCommand.SetEnabled, "mutation CorrelationId=mutation");
        await logger.LogErrorAsync("independent internal error");
        logger.Configure(RuntimeLogLevel.Information);
        await logger.LogRequestOperationAsync(PipeCommand.Ping, "verbose success");
        var content = File.ReadAllText(Active);
        Assert.DoesNotContain("routine success", content);
        Assert.Contains("CorrelationId=failed", content);
        Assert.Contains("CorrelationId=mutation", content);
        Assert.Contains("independent internal error", content);
        Assert.Contains("verbose success", content);
    }

    [Fact]
    public async Task ConcurrentWriters_RollWhileRunning_KeepWholeUnicodeEntriesAndBoundRetention()
    {
        var writers = Enumerable.Range(0, 4).Select(_ => new RollingLogFile(Active, 1024, 3)).ToArray();
        var entry = "菜单🙂\nException: example\n   at Stack.Trace()\n";
        await Task.WhenAll(writers.Select(async writer =>
        {
            for (var i = 0; i < 100; i++) Assert.True(await writer.AppendAsync(entry));
            return true;
        }));
        var files = Directory.GetFiles(_directory, "*.log");
        Assert.Equal(4, files.Length);
        foreach (var file in files)
        {
            Assert.InRange(new FileInfo(file).Length, 1, 1024);
            var content = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(file));
            Assert.Equal(string.Concat(Enumerable.Repeat(entry, content.Length / entry.Length)), content);
        }
        Assert.True(await writers[0].AppendAsync("HEAD\n" + new string('中', 5000) + "\nTAIL: at Stack.Trace()\n"));
        var giant = File.ReadAllText(Active);
        Assert.Contains("HEAD", giant);
        Assert.Contains("LogEntryTruncated", giant);
        Assert.Contains("TAIL: at Stack.Trace()", giant);
        Assert.InRange(new FileInfo(Active).Length, 1, 1024);
    }

    [Fact]
    public async Task CleanupAndFailedRotation_PreserveOwnershipAndContinueAfterClear()
    {
        var writer = new RollingLogFile(Active, 1024, 1);
        Assert.True(writer.Append(new string('x', 900)));
        Assert.True(writer.Append("next" + new string('y', 900)));
        var archive = Assert.Single(Directory.GetFiles(_directory, "*.archive-*.log"));
        var unrelated = Path.Combine(_directory, "backend.archive-unrelated.log");
        File.WriteAllText(unrelated, "other owner's evidence");
        var state = Path.Combine(_directory, "context-menu-state.json");
        File.WriteAllText(state, "state");
        using (var locked = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(writer.Append(new string('z', 900)));
            Assert.InRange(new FileInfo(Active).Length, 1, 1024);
            Assert.NotEmpty(await RuntimeLogFiles.ClearAsync(_directory));
            Assert.True(File.Exists(archive));
        }
        Assert.Empty(await RuntimeLogFiles.ClearAsync(_directory));
        Assert.True(writer.Append("continued"));
        Assert.Equal("continued", File.ReadAllText(Active));
        Assert.Equal("other owner's evidence", File.ReadAllText(unrelated));
        Assert.Equal("state", File.ReadAllText(state));
    }

    [Fact]
    public void LegacyAndSessionRecovery_PreserveRecentEvidenceAndDistinctSessions()
    {
        File.WriteAllText(Active, new string('x', 50_000) + "\nRECENT\n");
        var writer = new RollingLogFile(Active, 1024, 5);
        writer.Recover();
        var legacy = Assert.Single(Directory.GetFiles(_directory, "*.log"));
        Assert.Contains("LegacyOversizedLog", File.ReadAllText(legacy));
        Assert.Contains("RECENT", File.ReadAllText(legacy));
        Assert.InRange(new FileInfo(legacy).Length, 1, 1024);
        writer.BeginSession("first");
        Assert.True(writer.Append(new string('a', 700)));
        Assert.True(writer.Append(new string('b', 700)));
        var firstHeader = File.ReadLines(Active).First();
        // Omit EndSession to model unfinished ownership after a killed process.
        var next = new RollingLogFile(Active, 1024, 5);
        next.BeginSession("second");
        Assert.NotEqual(firstHeader, File.ReadLines(Active).First());
        Assert.Contains(Directory.GetFiles(_directory), file => file.Contains("-recovered-"));
        next.EndSession(normalShutdown: true);
        Assert.False(File.Exists(Active));
        Assert.Contains(Directory.GetFiles(_directory, "*.log"), file => File.ReadAllText(file).Contains("NormalShutdown=True"));
        Assert.All(Directory.GetFiles(_directory, "*.log"), file => Assert.InRange(new FileInfo(file).Length, 1, 1024));
    }

    private Process StartLegacyProcess(string script, string ready)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'\n" + script)));
        start.Environment["LOG_TEST_PATH"] = Active;
        start.Environment["LOG_TEST_READY"] = ready;
        return Process.Start(start)!;
    }

    private static async Task WaitForFileAsync(string path, Process process)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path) && !process.HasExited && timer.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10);
        Assert.True(File.Exists(path), "Legacy child process did not become ready.");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
