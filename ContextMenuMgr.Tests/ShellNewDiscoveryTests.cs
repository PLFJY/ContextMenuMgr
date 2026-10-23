using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using ContextMenuMgr.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace ContextMenuMgr.Tests;

// Permanent: native acquisition-stage attribution and authentication identity
// isolation. Discovery is read-only; these tests never write shell registrations.
public sealed class ShellNewDiscoveryTests(ITestOutputHelper output)
{
    [Fact]
    public void InvalidSession_FailsExplicitlyBeforeTokenOrMergedRootAcquisition()
    {
        var context = Context(-1);
        var logger = Logger();
        Assert.False(FrontendUserClassesRoot.TryOpen(context, logger, out var root, out var failure));
        Assert.Null(root);
        Assert.Contains("Stage=SessionValidation", failure);
        Assert.Contains("Win32Error=87", failure);
    }

    [Fact]
    public void NativeWtsFailure_IsNotMisreportedAsRegOpenUserClassesRootFailure()
    {
        using var current = WindowsIdentity.GetCurrent();
        var opened = FrontendUserClassesRoot.TryOpen(Context(Process.GetCurrentProcess().SessionId), Logger(), out var root, out var failure);
        using (root)
        {
            output.WriteLine(failure ?? "Opened the interactive user's merged HKCR.");
            if (!current.IsSystem)
            {
                Assert.False(opened);
                Assert.Contains("Stage=WTSQueryUserToken", failure);
                Assert.Contains("Win32Error=1314", failure);
            }
        }
        Assert.Null(WindowsIdentity.GetCurrent(ifImpersonating: true));
    }

    [Fact]
    public async Task FailedAuthoritativeAcquisition_CannotReturnEmptyOrPackagedOnlySnapshot()
    {
        var service = new SpecialMenuService(Logger());
        await Assert.ThrowsAnyAsync<Exception>(() => service.GetSnapshotAsync(SpecialMenuKind.ShellNew, Context(-1), CancellationToken.None));
    }

    [Fact]
    public async Task AuthenticatedPipeResolution_DoesNotFlowClientTokenAcrossAwait()
    {
        // Run in an isolated ExecutionContext so a regression cannot contaminate
        // the test runner's subsequent work or its response/reporting connections.
        await Task.Run(async () =>
        {
            var name = "ContextMenuMgr-141-auth-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut);
            var clientTask = Task.Run(() =>
            {
                using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                    PipeOptions.None, TokenImpersonationLevel.Impersonation);
                client.Connect(5000);
                client.WriteByte(1);
                client.Flush();
                Assert.Equal(1, client.ReadByte());
            });
            server.WaitForConnection();
            Assert.Equal(1, server.ReadByte());
            try
            {
                using var before = WindowsIdentity.GetCurrent(ifImpersonating: true);
                Assert.Null(before);
                var context = new BackendUserContextResolver(Logger()).TryResolveFromPipeClient(server);
                Assert.NotNull(context);
                Assert.Equal(Context(1).Sid, context.Sid);
                using var immediate = WindowsIdentity.GetCurrent(ifImpersonating: true);
                Assert.Null(immediate);
                await Task.Delay(10).ConfigureAwait(false);
                using var resumed = WindowsIdentity.GetCurrent(ifImpersonating: true);
                Assert.Null(resumed);
            }
            finally
            {
                server.WriteByte(1);
                await clientTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }).WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static BackendUserContext Context(int session)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new(identity.User!.Value, identity.Name, profile, profile, profile, session);
    }
    private static FileLogger Logger()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ContextMenuMgr.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var logger = new FileLogger(Path.Combine(directory, "backend.log"));
        logger.Configure(RuntimeLogLevel.Information);
        return logger;
    }
}
