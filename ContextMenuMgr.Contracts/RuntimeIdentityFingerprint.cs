using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ContextMenuMgr.Contracts;

/// <summary>Shared persisted identity algorithm; callers must supply their own verified user SID.</summary>
public static class RuntimeIdentityFingerprint
{
    public const int SchemaVersion = 1;
    public const string Kind = "WindowsMachineGuidAndUserSidHash";
    private const string Purpose = "ContextMenuMgr:HostIdentity:v1";

    public static string Compute(string machineGuid, string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineGuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Purpose}|{machineGuid}|{userSid}"))).ToLowerInvariant();
    }

    public static string Prefix(string fingerprint) => fingerprint[..Math.Min(12, fingerprint.Length)];

    [SupportedOSPlatform("windows")]
    public static string? ReadMachineGuid()
        => ReadMachineGuid(RegistryView.Registry64) ?? ReadMachineGuid(RegistryView.Default);

    [SupportedOSPlatform("windows")]
    private static string? ReadMachineGuid(RegistryView view)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid")?.ToString();
    }
}
