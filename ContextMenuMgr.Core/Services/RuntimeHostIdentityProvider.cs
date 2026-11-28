using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Services;

public sealed class RuntimeHostIdentityProvider
{
    private readonly FileLogger _logger;
    private readonly Func<string?> _resolveInteractiveUserSid;
    private readonly Lock _syncRoot = new();
    private RuntimeHostIdentity? _cachedIdentity;

    public RuntimeHostIdentityProvider(FileLogger logger, Func<string?> resolveInteractiveUserSid)
    {
        _logger = logger;
        _resolveInteractiveUserSid = resolveInteractiveUserSid;
    }

    public RuntimeHostIdentity GetCurrent()
    {
        lock (_syncRoot)
        {
            if (_cachedIdentity is { IsTrusted: true })
            {
                return _cachedIdentity;
            }

            _cachedIdentity = CreateIdentity();
            return _cachedIdentity;
        }
    }

    public bool IsCurrentFingerprint(string? fingerprint)
    {
        var current = GetCurrent();
        return current.IsTrusted
            && !string.IsNullOrWhiteSpace(fingerprint)
            && string.Equals(current.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private RuntimeHostIdentity CreateIdentity()
    {
        try
        {
            var machineGuid = RuntimeIdentityFingerprint.ReadMachineGuid();
            if (string.IsNullOrWhiteSpace(machineGuid))
            {
                return LogUntrusted("MachineGuidUnavailable");
            }

            var userSid = _resolveInteractiveUserSid();
            if (string.IsNullOrWhiteSpace(userSid))
            {
                return LogUntrusted("FrontendUserSidUnavailable");
            }

            var fingerprint = RuntimeIdentityFingerprint.Compute(machineGuid, userSid);
            var identity = new RuntimeHostIdentity(
                IsTrusted: true,
                SchemaVersion: RuntimeHostIdentity.CurrentSchemaVersion,
                Kind: RuntimeHostIdentity.CurrentKind,
                Fingerprint: fingerprint,
                FingerprintPrefix: RuntimeIdentityFingerprint.Prefix(fingerprint),
                CreatedAtUtc: DateTimeOffset.UtcNow,
                FailureReason: null);

            _logger.LogFireAndForget(
                $"PortableHostIdentityCheck: PackageKind={RuntimePaths.PackageKind}, CurrentFingerprintPrefix={identity.FingerprintPrefix}, Result=Trusted.");
            return identity;
        }
        catch (Exception ex)
        {
            _logger.LogFireAndForget(
                RuntimeLogLevel.Warning,
                $"PortableHostIdentityCheck: PackageKind={RuntimePaths.PackageKind}, Result=Untrusted, Reason={ex.GetType().Name}, Exception={ex.Message}.");
            return RuntimeHostIdentity.Untrusted(ex.GetType().Name);
        }
    }

    private RuntimeHostIdentity LogUntrusted(string reason)
    {
        _logger.LogFireAndForget(
            RuntimeLogLevel.Warning,
            $"PortableHostIdentityCheck: PackageKind={RuntimePaths.PackageKind}, CurrentFingerprintPrefix=untrusted, Result=Untrusted, Reason={reason}.");
        return RuntimeHostIdentity.Untrusted(reason);
    }

}
