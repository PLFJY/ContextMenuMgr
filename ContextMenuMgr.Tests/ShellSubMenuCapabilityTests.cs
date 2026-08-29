using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using Microsoft.Win32;
using Xunit;

namespace ContextMenuMgr.Tests;

public sealed class ShellSubMenuCapabilityTests
{
    [Fact]
    public void Capability_OnlyAppearsForRegistryDefinedChildShellVerb()
    {
        var relative = $@"Software\Classes\ContextMenuMgr.Tests\SubMenu\{Guid.NewGuid():N}";
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var backendPath = $@"HKEY_USERS\{sid}\{relative}";
        try
        {
            using var parent = Registry.CurrentUser.CreateSubKey(relative, writable: true)!;
            Assert.False(ContextMenuRegistryCatalog.HasManageableSubMenuItems(parent, backendPath));
            using var shell = parent.CreateSubKey("shell", writable: true)!;
            using var child = shell.CreateSubKey("Child", writable: true)!;
            Assert.True(ContextMenuRegistryCatalog.HasManageableSubMenuItems(parent, backendPath));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(relative, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Capability_ResolvesExtendedSubCommandsKeyInFrontendUserClasses()
    {
        var token = Guid.NewGuid().ToString("N");
        var parentRelative = $@"Software\Classes\ContextMenuMgr.Tests\SubMenu\Parent{token}";
        var targetRelative = $@"Software\Classes\ContextMenuMgr.Tests\SubMenu\Target{token}\shell\Child";
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        try
        {
            using var parent = Registry.CurrentUser.CreateSubKey(parentRelative, writable: true)!;
            parent.SetValue("ExtendedSubCommandsKey", $@"ContextMenuMgr.Tests\SubMenu\Target{token}");
            using var child = Registry.CurrentUser.CreateSubKey(targetRelative, writable: true)!;
            Assert.True(ContextMenuRegistryCatalog.HasManageableSubMenuItems(parent, $@"HKEY_USERS\{sid}\{parentRelative}"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\ContextMenuMgr.Tests\SubMenu\Parent{token}", false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\ContextMenuMgr.Tests\SubMenu\Target{token}", false);
        }
    }
}
