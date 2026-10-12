using System.Text;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class RollingLogFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-log-tests-" + Guid.NewGuid().ToString("N"));
    private string Active => Path.Combine(_directory, "backend.log");

    public RollingLogFileTests() => Directory.CreateDirectory(_directory);

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

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
