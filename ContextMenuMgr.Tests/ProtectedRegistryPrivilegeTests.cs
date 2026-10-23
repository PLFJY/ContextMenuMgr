using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using ContextMenuMgr.Backend.Services;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace ContextMenuMgr.Tests;

// Permanent: real token access checks, partial acquisition and concurrent scopes.
// Token DACL/privilege changes run ONLY in a disposable child testhost, never in
// the parent test runner or an installed service. No machine policy is modified.
public sealed class ProtectedRegistryPrivilegeTests
{
    private const string ChildScenario = "CONTEXTMENUMGR_TOKEN_TEST";

    [Theory]
    [InlineData("deny-open")]
    [InlineData("deny-adjust")]
    [InlineData("missing-second")]
    [InlineData("deny-private-adjust")]
    public async Task TokenFailures_AreIsolatedAndAttributed(string scenario)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(ProtectedRegistryPrivilegeTests).Assembly.Location);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=ContextMenuMgr.Tests.ProtectedRegistryPrivilegeTests.IsolatedTokenWorker");
        start.Environment[ChildScenario] = scenario;
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { child.Kill(entireProcessTree: true); throw; }
        Assert.True(child.ExitCode == 0, await stdout + await stderr);
    }

    [TokenWorkerFact]
    public void IsolatedTokenWorker()
    {
        var scenario = Environment.GetEnvironmentVariable(ChildScenario);
        if (scenario is null) return; // Invoked explicitly by the parent theory.
        using var token = NativeToken.Open(0x20000 | 0x40000 | 0x80 | 0x20 | 0x8 | 0x2 | 0x4);
        var before = NativeToken.Privileges(token);
        if (scenario == "deny-private-adjust")
        {
            NativeToken.DenyPrivateAdjustment(token);
            var failure = Assert.Throws<ProtectedRegistryMutationException>(() => ScopedRegistryPrivileges.Run(() => Assert.Fail("Mutation ran without enabling privileges.")));
            Assert.Contains("Phase=OpenPrivateThreadToken", failure.Message);
            Assert.Equal(5, Assert.IsType<Win32Exception>(failure.InnerException).NativeErrorCode);
            Assert.Equal(before, NativeToken.Privileges(token));
            return;
        }
        if (scenario == "missing-second")
        {
            NativeToken.Adjust(token, "SeRestorePrivilege", 4 /* permanently remove, child only */);
            var failure = Assert.Throws<ProtectedRegistryMutationException>(() => ScopedRegistryPrivileges.Run(() => Assert.Fail("Mutation ran without required privileges.")));
            Assert.Contains("PrivilegeAbsent", failure.ToString());
            Assert.Contains(before.ContainsKey("SeTakeOwnershipPrivilege") ? "SeRestorePrivilege" : "SeTakeOwnershipPrivilege", failure.ToString());
            Assert.Contains("Win32Error=1300", failure.ToString());
            Assert.Equal(NativeToken.Privileges(token), NativeToken.ProcessPrivileges());
            Assert.Equal(before.GetValueOrDefault("SeTakeOwnershipPrivilege"), NativeToken.Privileges(token).GetValueOrDefault("SeTakeOwnershipPrivilege"));
            return;
        }

        var denied = scenario == "deny-open" ? 0x2 : 0x20;
        NativeToken.DenyAccess(token, denied);
        // Establish the same native error as the reported old ADJUST|QUERY open.
        Assert.False(NativeToken.OpenProcessToken(new IntPtr(-1), (uint)(denied | 8), out var refused));
        Assert.Equal(5, Marshal.GetLastWin32Error());
        refused.Dispose();
        var invoked = false;
        var exception = Record.Exception(() => ScopedRegistryPrivileges.Run(() => invoked = true));
        if (scenario == "deny-open")
        {
            var failure = Assert.IsType<ProtectedRegistryMutationException>(exception);
            Assert.Contains("Phase=OpenProcessToken", failure.Message);
            Assert.Equal(5, Assert.IsType<Win32Exception>(failure.InnerException).NativeErrorCode);
            Assert.False(invoked);
        }
        else if (before.ContainsKey("SeTakeOwnershipPrivilege") && before.ContainsKey("SeRestorePrivilege"))
        {
            Assert.Null(exception);
            Assert.True(invoked);
        }
        else
        {
            // Unelevated tokens lack ownership/restore privileges. Opening the
            // primary token still succeeded despite its ADJUST_PRIVILEGES denial.
            Assert.Contains("PrivilegeAbsent", Assert.IsType<ProtectedRegistryMutationException>(exception).ToString());
            Assert.False(invoked);
        }
        Assert.Equal(before, NativeToken.Privileges(token));
        using var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true);
        Assert.Null(impersonated);
    }

    [Fact]
    public void IncomingImpersonation_IsNotUsedForAcquisition_AndIsRestoredOnFailure()
    {
        using var primary = NativeToken.Open(0x2 | 0x8);
        var before = NativeToken.Privileges(primary);
        Assert.True(NativeToken.DuplicateTokenEx(primary, 0x8 | 0x4 | 0x20, IntPtr.Zero, 2, 2, out var caller));
        using (caller)
        {
            NativeToken.Adjust(caller, "SeTakeOwnershipPrivilege", 4);
            NativeToken.Adjust(caller, "SeRestorePrivilege", 4);
            var callerBefore = NativeToken.Privileges(caller);
            WindowsIdentity.RunImpersonated(caller, () =>
            {
                var invoked = false;
                var failure = Record.Exception(() => ScopedRegistryPrivileges.Run(() => invoked = true));
                Assert.Equal(before.Count == 2, invoked);
                if (invoked) Assert.Null(failure);
                else Assert.Contains("IncomingContext=[", Assert.IsType<ProtectedRegistryMutationException>(failure).Message);
                using var restored = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                Assert.Equal(callerBefore, NativeToken.Privileges(restored.AccessToken));
            });
        }
        Assert.Equal(before, NativeToken.Privileges(primary));
        using var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true);
        Assert.Null(impersonated);
    }

    private sealed class TokenWorkerFactAttribute : FactAttribute
    {
        public TokenWorkerFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(ChildScenario) is null)
                Skip = "Executed by TokenFailures_AreIsolatedAndAttributed in a disposable child testhost.";
        }
    }

    [RegistryPrivilegeFact]
    public async Task ConcurrentAndFailedScopes_LeaveProcessAndCallerTokensUntouched()
    {
        var processBefore = NativeToken.ProcessPrivileges();
        using var primary = NativeToken.Open(0x2 | 0x8);
        Assert.True(NativeToken.DuplicateTokenEx(primary, 0x8 | 0x4 | 0x20, IntPtr.Zero, 2, 2, out var caller));
        using (caller)
        {
            NativeToken.Adjust(caller, "SeTakeOwnershipPrivilege", 0);
            NativeToken.Adjust(caller, "SeRestorePrivilege", 0);
            var callerBefore = NativeToken.Privileges(caller);
            using var overlap = new Barrier(2);
            var workers = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
                WindowsIdentity.RunImpersonated(caller, () =>
                {
                    var failure = Record.Exception(() => ScopedRegistryPrivileges.Run(() =>
                    {
                        using var effective = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                        var privileges = NativeToken.Privileges(effective.AccessToken);
                        Assert.NotEqual(0u, privileges["SeTakeOwnershipPrivilege"] & 2);
                        Assert.NotEqual(0u, privileges["SeRestorePrivilege"] & 2);
                        Assert.True(overlap.SignalAndWait(TimeSpan.FromSeconds(10)));
                        if (index == 0) throw new IOException("Fail after both privileges were enabled.");
                    }));
                    if (index == 0) Assert.IsType<IOException>(failure);
                    else Assert.Null(failure);
                    using var restored = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                    Assert.Equal(callerBefore, NativeToken.Privileges(restored.AccessToken));
                }))).ToArray();
            await Task.WhenAll(workers);
            Assert.Equal(callerBefore, NativeToken.Privileges(caller));
        }
        Assert.Equal(processBefore, NativeToken.ProcessPrivileges());
    }

    internal sealed class RegistryPrivilegeFactAttribute : FactAttribute
    {
        public RegistryPrivilegeFactAttribute()
        {
            var privileges = NativeToken.ProcessPrivileges();
            if (!privileges.ContainsKey("SeTakeOwnershipPrivilege") || !privileges.ContainsKey("SeRestorePrivilege"))
                Skip = "Requires an elevated disposable testhost with ownership/restore privileges; no automatic elevation.";
        }
    }

    internal static class NativeToken
    {
        internal static SafeAccessTokenHandle Open(uint access)
        {
            if (!OpenProcessToken(new IntPtr(-1), access, out var token)) throw new Win32Exception();
            return token;
        }

        internal static Dictionary<string, uint> ProcessPrivileges()
        {
            using var token = Open(8);
            return Privileges(token);
        }

        internal static Dictionary<string, uint> Privileges(SafeAccessTokenHandle token)
        {
            _ = GetTokenInformation(token, 3, IntPtr.Zero, 0, out var size);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Assert.True(GetTokenInformation(token, 3, buffer, size, out _));
                var result = new Dictionary<string, uint>();
                for (var i = 0; i < Marshal.ReadInt32(buffer); i++)
                {
                    var entry = Marshal.PtrToStructure<Entry>(buffer + 4 + i * Marshal.SizeOf<Entry>());
                    foreach (var name in new[] { "SeTakeOwnershipPrivilege", "SeRestorePrivilege" })
                    {
                        Assert.True(LookupPrivilegeValue(null, name, out var id));
                        if (id == entry.Id) result[name] = entry.Attributes;
                    }
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        internal static void Adjust(SafeAccessTokenHandle token, string privilege, uint attributes)
        {
            Assert.True(LookupPrivilegeValue(null, privilege, out var id));
            var state = new PrivilegesState { Count = 1, Entry = new Entry { Id = id, Attributes = attributes } };
            Assert.True(AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero));
        }

        internal static void DenyAccess(SafeAccessTokenHandle token, int rights)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var acl = new RawAcl(2, 2);
            acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessDenied, rights, identity.User!, false, null));
            acl.InsertAce(1, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0xF01FF, identity.User!, false, null));
            var descriptor = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, identity.User, identity.User, null, acl);
            var binary = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(binary, 0);
            Assert.True(SetKernelObjectSecurity(token, 4, binary));
        }

        internal static void DenyPrivateAdjustment(SafeAccessTokenHandle token)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var acl = new RawAcl(2, 2);
            acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessDenied, 0x20, identity.User!, false, null));
            acl.InsertAce(1, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0xF01FF, identity.User!, false, null));
            var binary = new byte[acl.BinaryLength];
            acl.GetBinaryForm(binary, 0);
            var aclBuffer = Marshal.AllocHGlobal(binary.Length);
            var defaultDacl = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.Copy(binary, 0, aclBuffer, binary.Length);
                Marshal.WriteIntPtr(defaultDacl, aclBuffer);
                Assert.True(SetTokenInformation(token, 6 /* TokenDefaultDacl */, defaultDacl, IntPtr.Size));
            }
            finally { Marshal.FreeHGlobal(defaultDacl); Marshal.FreeHGlobal(aclBuffer); }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct Entry { public long Id; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PrivilegesState { public uint Count; public Entry Entry; }

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle copy);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr buffer, int length, out int required);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValue(string? system, string name, out long id);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref PrivilegesState state, uint size, IntPtr previous, IntPtr length);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetKernelObjectSecurity(SafeAccessTokenHandle handle, uint information, byte[] descriptor);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetTokenInformation(SafeAccessTokenHandle handle, int information, IntPtr buffer, int length);
    }
}
