namespace ContextMenuMgr.Backend.Services;

internal enum ShellNewPhysicalSource
{
    Unresolved,
    User,
    Machine
}

/// <summary>
/// Maps the effective registration observed through HKCR to a writable physical
/// source. The input facts are obtained from Windows' merged view plus explicit
/// physical probes; this type does not implement HKCR merging itself.
/// </summary>
internal static class ShellNewPhysicalSourceResolver
{
    public static ShellNewPhysicalSource Resolve(bool effectiveRegistrationExists, bool userRegistrationExists, bool machineRegistrationExists)
    {
        if (!effectiveRegistrationExists)
        {
            return ShellNewPhysicalSource.Unresolved;
        }

        return userRegistrationExists
            ? ShellNewPhysicalSource.User
            : machineRegistrationExists
                ? ShellNewPhysicalSource.Machine
                : ShellNewPhysicalSource.Unresolved;
    }
}
