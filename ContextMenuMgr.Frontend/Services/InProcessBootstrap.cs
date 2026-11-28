#if CONTEXTMENUMGR_IN_PROCESS
using System.IO;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Frontend.Services;

internal static class InProcessBootstrap
{
    private static readonly Lock Gate = new();
    private static PortableInteractiveUser? _user;

    public static PortableInteractiveUser User
    {
        get { lock (Gate) return _user ?? throw new InvalidOperationException("The local Portable startup checks have not completed."); }
    }

    public static void Prepare()
    {
        lock (Gate)
        {
            if (_user is not null) return;
            if (!RuntimeHosting.IsInProcess || RuntimePaths.PackageKind != RuntimePackageKind.Portable)
                throw new InvalidOperationException("The local Portable startup checks require the dedicated In-process build.");

            VerifyDataRoot();
            var serviceState = PortableServiceStartup.Observe(
                Path.Combine(AppContext.BaseDirectory, "ContextMenuManagerPlus.Service.exe"));
            if (serviceState != PortableServiceRegistrationState.Absent)
                throw new InvalidOperationException($"A Windows Service registration for Context Menu Manager Plus is present or unresolved ({serviceState}). Resolve it before starting In-process Portable. The runtime will not take over its state or registry work.");

            _user = PortableInteractiveUser.Resolve();
        }
    }

    private static void VerifyDataRoot()
    {
        var root = Path.GetFullPath(RuntimePaths.DataDirectory);
        var applicationData = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Data"));
        if (!string.Equals(root, applicationData, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Portable data must remain inside the application Data directory.");
        var directory = new DirectoryInfo(root);
        if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The application Data directory is a reparse point. Resolve it before starting the elevated Portable runtime.");
    }
}
#endif
