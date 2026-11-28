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
        if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0
            && !IsExpectedScoopPersistenceTarget(directory))
            throw new InvalidOperationException("The application Data reparse point is not the matching Scoop package's persistent Data directory.");
    }

    private static bool IsExpectedScoopPersistenceTarget(DirectoryInfo dataDirectory)
    {
        try
        {
            var appDirectory = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
            var packageVersion = appDirectory.Parent;
            var packageDirectory = packageVersion?.Parent;
            var appsDirectory = packageDirectory?.Parent;
            if (packageDirectory is null || !string.Equals(appsDirectory?.Name, "apps", StringComparison.OrdinalIgnoreCase)
                || (!string.Equals(packageDirectory.Name, "contextmenumgrplus", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(packageDirectory.Name, "contextmenumgrplus-beta", StringComparison.OrdinalIgnoreCase)))
                return false;

            var scoopRoot = appsDirectory!.Parent;
            if (scoopRoot is null) return false;
            var expectedTarget = Path.GetFullPath(Path.Combine(scoopRoot.FullName, "persist", packageDirectory.Name, "Data"));
            var target = dataDirectory.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !string.Equals(Path.GetFullPath(target.FullName), expectedTarget, StringComparison.OrdinalIgnoreCase))
                return false;

            // Only the version's Data entry may be a reparse point; persistence parents must be ordinary directories.
            for (var current = new DirectoryInfo(expectedTarget); current is not null; current = current.Parent)
            {
                if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (string.Equals(Path.GetFullPath(current.FullName), Path.GetFullPath(scoopRoot.FullName), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
#endif
