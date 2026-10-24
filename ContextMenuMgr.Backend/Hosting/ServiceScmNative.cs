using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Hosting;

internal static class ServiceScmNative
{
    private const int QueryConfigAccess = 1, ChangeConfigAccess = 2, QueryStatusAccess = 4, StartAccess = 0x10;
    private const uint NoChange = uint.MaxValue;
    private const int ConfigurationBufferSize = 8192;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenSCManager(string? machineName, string? databaseName, int desiredAccess);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr OpenService(IntPtr scm, string serviceName, int desiredAccess);
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool DeleteService(IntPtr service);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryServiceConfig(SafeScHandle service, IntPtr buffer, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(SafeScHandle service, int level, out ServiceStatusProcess status, int size, out int needed);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateService(SafeScHandle scm, string name, string displayName, int access,
        uint type, uint start, uint errorControl, string binaryPath, string? group, IntPtr tag,
        string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfig(SafeScHandle service, uint type, uint start, uint errorControl,
        string? binaryPath, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? displayName);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfig2(SafeScHandle service, int level, IntPtr information);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryServiceConfig2(SafeScHandle service, int level, IntPtr information, int size, out int needed);

    internal static string CreateOwnService(string binaryPath, string startupMode)
    {
        using var scm = new SafeScHandle(OpenSCManager(null, null, 3)); // CONNECT | CREATE_SERVICE
        if (scm.IsInvalid) ThrowLastError("OpenSCManager(CreateService)");
        using var service = new SafeScHandle(CreateService(scm, ServiceMetadata.ServiceName, ServiceMetadata.DisplayName,
            QueryConfigAccess, 0x10, StartupType(startupMode), 1, binaryPath, null, IntPtr.Zero, null, null, null));
        if (service.IsInvalid) ThrowLastError("CreateService");
        return "Created";
    }

    internal static string ConfigureOwnImagePath(string binaryPath)
    {
        using var service = OpenManagedService(ServiceMetadata.ServiceName, QueryConfigAccess | ChangeConfigAccess);
        if (ReadBaseConfiguration(service).ImagePath == binaryPath) return "Unchanged";
        if (!ChangeServiceConfig(service, NoChange, NoChange, NoChange, binaryPath, null, IntPtr.Zero, null, null, null, null))
            ThrowLastError("ChangeServiceConfig(ImagePath)");
        if (ReadBaseConfiguration(service).ImagePath != binaryPath) throw new IOException("SCM ImagePath read-back mismatch.");
        return "Updated";
    }

    internal static string ConfigureOwnDescription(string description)
    {
        using var service = OpenManagedService(ServiceMetadata.ServiceName, QueryConfigAccess | ChangeConfigAccess);
        if (ReadDescription(service) == description) return "Unchanged";
        var text = Marshal.StringToHGlobalUni(description);
        try
        {
            ChangeOptionalConfiguration(service, 1, new NativeServiceDescription { Description = text });
            if (ReadDescription(service) != description) throw new IOException("SCM description read-back mismatch.");
        }
        finally { Marshal.FreeHGlobal(text); }
        return "Updated";
    }

    internal static string ConfigureOwnStartupMode(string startupMode)
    {
        var desiredType = StartupType(startupMode);
        var delayed = startupMode == "delayed-auto";
        using var service = OpenManagedService(ServiceMetadata.ServiceName, QueryConfigAccess | ChangeConfigAccess);
        var previous = ReadBaseConfiguration(service);
        var wasDelayed = ReadOptionalConfiguration(service, 3, static p => Marshal.ReadInt32(p) != 0);
        if (previous.StartType == desiredType && wasDelayed == delayed) return "Unchanged";
        try
        {
            if (previous.StartType != desiredType
                && !ChangeServiceConfig(service, NoChange, desiredType, NoChange, null, null, IntPtr.Zero, null, null, null, null))
                ThrowLastError("ChangeServiceConfig(StartType)");
            if (wasDelayed != delayed) ChangeOptionalConfiguration(service, 3, delayed ? 1 : 0);
        }
        catch (Exception original)
        {
            // Auto/delayed-auto requires two APIs. If the second fails, restore
            // our observed intermediate state before the outer policy rollback.
            // Never overwrite a different owner's/configurator's startup mode.
            try
            {
                var current = ReadBaseConfiguration(service);
                var currentDelayed = ReadOptionalConfiguration(service, 3, static p => Marshal.ReadInt32(p) != 0);
                if (current.ImagePath != previous.ImagePath || current.ServiceType != previous.ServiceType || current.Account != previous.Account
                    || current.StartType != previous.StartType && current.StartType != desiredType)
                    throw new IOException("External SCM configuration change preserved.");
                if (current.StartType != previous.StartType
                    && !ChangeServiceConfig(service, NoChange, previous.StartType, NoChange, null, null, IntPtr.Zero, null, null, null, null))
                    ThrowLastError("ChangeServiceConfig(StartupRollback)");
                if (currentDelayed != wasDelayed) ChangeOptionalConfiguration(service, 3, wasDelayed ? 1 : 0);
                if (ReadBaseConfiguration(service) != previous
                    || ReadOptionalConfiguration(service, 3, static p => Marshal.ReadInt32(p) != 0) != wasDelayed)
                    throw new IOException("SCM startup rollback read-back mismatch.");
            }
            catch (Exception rollback)
            {
                throw new InvalidOperationException($"Startup configuration failed: {original.Message}; rollback uncertain: {rollback.Message}",
                    new AggregateException(original, rollback));
            }
            throw;
        }
        // Callers verify the complete startup snapshot before committing policy.
        return "Updated";
    }

    internal static string ConfigureOwnRecovery()
    {
        using var service = OpenManagedService(ServiceMetadata.ServiceName, QueryConfigAccess | ChangeConfigAccess | StartAccess);
        var previous = ReadRecovery(service);
        if (previous.IsConfigured) return "Unchanged";
        if (!previous.HasExpectedActions)
        {
            var actions = ServiceRecoveryConfiguration.RequiredActions;
            var buffer = Marshal.AllocHGlobal(checked(actions.Count * Marshal.SizeOf<ServiceRecoveryAction>()));
            try
            {
                for (var index = 0; index < actions.Count; index++)
                    Marshal.StructureToPtr(actions[index], IntPtr.Add(buffer, index * Marshal.SizeOf<ServiceRecoveryAction>()), false);
                ChangeOptionalConfiguration(service, 2, new NativeFailureActions
                {
                    ResetPeriod = ServiceRecoveryConfiguration.RequiredResetSeconds,
                    ActionCount = checked((uint)actions.Count), Actions = buffer
                });
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        if (!previous.OnNonCrashFailures) ChangeOptionalConfiguration(service, 4, 1);
        var actual = ReadRecovery(service);
        if (!actual.IsConfigured)
            throw new IOException($"SCM recovery read-back mismatch: ResetSeconds={actual.ResetSeconds}, "
                + $"Actions=[{string.Join(',', actual.Actions.Select(action => $"{action.Type}/{action.DelayMilliseconds}"))}], "
                + $"OnNonCrashFailures={actual.OnNonCrashFailures}.");
        return "Updated";
    }

    internal static string DisableRecoveryDuringRemoval(string serviceName)
    {
        using var service = OpenManagedService(serviceName, QueryConfigAccess | ChangeConfigAccess);
        if (!ReadOptionalConfiguration(service, 4, static p => Marshal.ReadInt32(p) != 0)) return "Unchanged";
        ChangeOptionalConfiguration(service, 4, 0);
        return "Updated";
    }

    internal static ScmServiceConfiguration ReadConfiguration(string serviceName)
    {
        using var service = OpenManagedService(serviceName, QueryConfigAccess | QueryStatusAccess);
        var config = ReadBaseConfiguration(service);
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
            ThrowLastError("QueryServiceStatusEx");
        var delayed = ReadOptionalConfiguration(service, 3, static p => Marshal.ReadInt32(p) != 0);
        return new(config.ImagePath, config.StartType, delayed, ReadRecovery(service), status.CurrentState);
    }

    private static SafeScHandle OpenManagedService(string serviceName, int access)
    {
        // Configuration helpers only manage this product's current/legacy names.
        // Self-removal uses its separate, strictly current-service-only path.
        if (serviceName != ServiceMetadata.ServiceName && serviceName != ServiceMetadata.LegacyServiceName)
            throw new ArgumentException("Unsupported service name.", nameof(serviceName));
        using var scm = new SafeScHandle(OpenSCManager(null, null, 1));
        if (scm.IsInvalid) ThrowLastError("OpenSCManager");
        var service = new SafeScHandle(OpenService(scm.DangerousGetHandle(), serviceName, access));
        if (!service.IsInvalid) return service;
        var error = Marshal.GetLastWin32Error();
        service.Dispose();
        throw new Win32Exception(error, $"OpenService({serviceName}): {new Win32Exception(error).Message}");
    }

    private static uint StartupType(string mode) => mode switch
    {
        "auto" or "delayed-auto" => 2, "demand" => 3, "disabled" => 4,
        _ => throw new ArgumentException("Unsupported service startup mode.", nameof(mode))
    };

    private static (string ImagePath, uint StartType, uint ServiceType, string? Account) ReadBaseConfiguration(SafeScHandle service)
    {
        // Windows caps QUERY_SERVICE_CONFIG at 8 KiB. One bounded allocation
        // avoids a size-probe RPC and never retries an uncertain mutation.
        var buffer = Marshal.AllocHGlobal(ConfigurationBufferSize);
        try
        {
            if (!QueryServiceConfig(service, buffer, ConfigurationBufferSize, out _)) ThrowLastError("QueryServiceConfig");
            var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
            return (Marshal.PtrToStringUni(config.BinaryPath) ?? "", config.StartType, config.ServiceType, Marshal.PtrToStringUni(config.Account));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static T ReadOptionalConfiguration<T>(SafeScHandle service, int level, Func<IntPtr, T> read)
    {
        var buffer = Marshal.AllocHGlobal(ConfigurationBufferSize);
        try
        {
            if (!QueryServiceConfig2(service, level, buffer, ConfigurationBufferSize, out _))
                ThrowLastError($"QueryServiceConfig2(Level={level})");
            return read(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ReadDescription(SafeScHandle service)
        => ReadOptionalConfiguration(service, 1, static p => Marshal.PtrToStringUni(Marshal.ReadIntPtr(p)) ?? "");

    private static ServiceRecoveryConfiguration ReadRecovery(SafeScHandle service)
    {
        var (reset, actions) = ReadOptionalConfiguration(service, 2, static buffer =>
        {
            var native = Marshal.PtrToStructure<NativeFailureActions>(buffer);
            var size = Marshal.SizeOf<ServiceRecoveryAction>();
            var offset = native.Actions.ToInt64() - buffer.ToInt64();
            // Validate the SCM-owned array before marshalling, including empty
            // policy and malformed/oversized configuration.
            if (native.ActionCount > ConfigurationBufferSize / size
                || native.ActionCount != 0 && (offset < Marshal.SizeOf<NativeFailureActions>()
                    || offset > ConfigurationBufferSize - (long)native.ActionCount * size))
                throw new IOException("Invalid SCM failure-action buffer.");
            var result = new ServiceRecoveryAction[native.ActionCount];
            for (var index = 0; index < result.Length; index++)
                result[index] = Marshal.PtrToStructure<ServiceRecoveryAction>(IntPtr.Add(native.Actions, index * size));
            return (native.ResetPeriod, result);
        });
        return new(reset, actions, ReadOptionalConfiguration(service, 4, static p => Marshal.ReadInt32(p) != 0));
    }

    private static void ChangeOptionalConfiguration<T>(SafeScHandle service, int level, T value) where T : struct
    {
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(value, buffer, false);
            if (!ChangeServiceConfig2(service, level, buffer)) ThrowLastError($"ChangeServiceConfig2(Level={level})");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void ThrowLastError(string stage)
    {
        var error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"{stage}: {new Win32Exception(error).Message}");
    }

    internal static (string ImagePath, uint StartType, uint ProcessId) ReadRunningRegistration(SafeScHandle service)
    {
        var config = ReadBaseConfiguration(service);
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
            ThrowLastError("QueryServiceStatusEx");
        // Only a dedicated, running LocalSystem Win32 service can own this operation.
        if (config.ServiceType != 0x10 || status.ServiceType != 0x10
            || status.CurrentState is not (4 or 3)
            || !string.Equals(config.Account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Service type, account or running state is uncertain.");
        return (config.ImagePath, config.StartType, status.ProcessId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        public uint ServiceType, StartType, ErrorControl;
        public IntPtr BinaryPath, LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies, Account, DisplayName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFailureActions
    {
        public uint ResetPeriod;
        public IntPtr RebootMessage, Command;
        public uint ActionCount;
        public IntPtr Actions;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeServiceDescription { public IntPtr Description; }

    internal sealed class SafeScHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeScHandle(IntPtr value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
}

internal sealed record ScmServiceConfiguration(string ImagePath, uint StartType, bool DelayedAutoStart,
    ServiceRecoveryConfiguration Recovery, uint State);

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ServiceRecoveryAction(uint Type, uint DelayMilliseconds);

internal sealed record ServiceRecoveryConfiguration(uint ResetSeconds, IReadOnlyList<ServiceRecoveryAction> Actions, bool OnNonCrashFailures)
{
    public const uint RequiredResetSeconds = 86400;
    public static IReadOnlyList<ServiceRecoveryAction> RequiredActions { get; } = Array.AsReadOnly<ServiceRecoveryAction>(
        [new(1, 5000), new(1, 15000), new(1, 60000)]); // SC_ACTION_RESTART
    public bool HasExpectedActions => ResetSeconds == RequiredResetSeconds && Actions.SequenceEqual(RequiredActions);
    public bool IsConfigured => HasExpectedActions && OnNonCrashFailures;
}
