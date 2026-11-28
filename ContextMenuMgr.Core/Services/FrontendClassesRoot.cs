using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

internal delegate bool TryOpenFrontendClassesRoot(BackendUserContext context, FileLogger logger,
    out IFrontendClassesRoot? root, out string? failure);

internal interface IFrontendClassesRoot : IDisposable
{
    RegistryKey ClassesRoot { get; }
}
