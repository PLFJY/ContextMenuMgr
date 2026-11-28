using System.ComponentModel;
using System.Text.Json;
using ContextMenuMgr.Contracts;
using ContextMenuMgr.Frontend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class DeepAnalysisLifecycleTests
{
    [Fact]
    public async Task LaunchFailureReturnsStructuredErrorAndRemovesSerializedRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ContextMenuMgr-Probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new FrontendSettingsService(null, Path.Combine(root, "settings.json"));
            var service = new ContextMenuDeepAnalysisService(new LocalizationService(settings));
            var request = new ContextMenuDeepAnalysisRequest { OperationId = Guid.NewGuid(), ItemId = "fixture" };
            var probePath = Path.Combine(root, "ProbeHost.exe");
            var selection = new ProbeHostSelection(probePath, ProbeHostArchitecture.X64, null, false, "unknown",
                null, "fixture", null);
            string? requestPath = null;
            var attempts = 0;

            var result = await service.RunProbeHostAsync(request, selection, root, DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(15), directory => Directory.CreateDirectory(directory), info =>
                {
                    attempts++;
                    Assert.Equal(probePath, info.FileName);
                    Assert.Equal("--request", info.ArgumentList[0]);
                    requestPath = info.ArgumentList[1];
                    var serialized = JsonSerializer.Deserialize<ContextMenuDeepAnalysisRequest>(
                        File.ReadAllText(requestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    Assert.Equal(request.OperationId, serialized!.OperationId);
                    throw new Win32Exception(5, "Controlled ProbeHost launch failure.");
                }, default);

            Assert.Equal(1, attempts);
            Assert.False(result.Success);
            Assert.Equal("ProbeHostStartFailed", result.ErrorCode);
            Assert.Equal("Controlled ProbeHost launch failure.", result.Message);
            Assert.Equal(request.OperationId, result.OperationId);
            Assert.NotNull(requestPath);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
