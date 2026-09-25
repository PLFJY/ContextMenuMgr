using ContextMenuMgr.Backend.Services;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class PackagedContextMenuScanCacheTests
{
    [Fact]
    public async Task ConcurrentRequestsForSameSid_ShareOneScan()
    {
        var scanCount = 0;
        using var release = new ManualResetEventSlim();
        var cache = new PackagedContextMenuScanCache(_ =>
        {
            Interlocked.Increment(ref scanCount);
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return [];
        });

        var first = cache.GetAsync("S-1-5-21-100", CancellationToken.None);
        var second = cache.GetAsync("S-1-5-21-100", CancellationToken.None);
        release.Set();

        await Task.WhenAll(first, second);
        Assert.Equal(1, scanCount);
    }

    [Fact]
    public async Task ExpiredResult_IsScannedAgain()
    {
        var scanCount = 0;
        var cache = new PackagedContextMenuScanCache(_ =>
        {
            Interlocked.Increment(ref scanCount);
            return [];
        }, TimeSpan.Zero);

        await cache.GetAsync("S-1-5-21-100", CancellationToken.None);
        await cache.GetAsync("S-1-5-21-100", CancellationToken.None);

        Assert.Equal(2, scanCount);
    }

    [Fact]
    public async Task DifferentSids_DoNotShareResults()
    {
        var scannedSids = new List<string>();
        var cache = new PackagedContextMenuScanCache(sid =>
        {
            lock (scannedSids)
            {
                scannedSids.Add(sid);
            }

            return [];
        });

        await cache.GetAsync("S-1-5-21-100", CancellationToken.None);
        await cache.GetAsync("S-1-5-21-200", CancellationToken.None);

        Assert.Equal(2, scannedSids.Count);
        Assert.Contains("S-1-5-21-100", scannedSids);
        Assert.Contains("S-1-5-21-200", scannedSids);
    }

    [Fact]
    public async Task CanceledCaller_DoesNotCancelSharedScan()
    {
        using var release = new ManualResetEventSlim();
        var cache = new PackagedContextMenuScanCache(_ =>
        {
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return [];
        });
        using var cancellation = new CancellationTokenSource();

        var canceledCall = cache.GetAsync("S-1-5-21-100", cancellation.Token);
        var survivingCall = cache.GetAsync("S-1-5-21-100", CancellationToken.None);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCall);
        release.Set();

        Assert.Empty(await survivingCall);
    }
}
