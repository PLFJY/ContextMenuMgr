using ContextMenuMgr.Contracts;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ContextMenuMgr.Backend.Services;

/// <summary>
/// Owns durable registration of the in-process ShellProxy. The registry is the source of truth;
/// no state is kept under the normal ContextMenuMgr runtime directory.
/// </summary>
public sealed class ShellProxyManager
{
    internal const string MetadataKeyName = "ContextMenuMgrShellProxy";
    private const int SchemaVersion = 1;
    private const string ProxyFileName = "ContextMenuMgr.ShellProxy.dll";
    private readonly FileLogger _logger;

    public ShellProxyManager(FileLogger logger) => _logger = logger;

    public async Task<PipeResponse> CreateAsync(ShellProxyWrapperRequest request, BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        var item = request.Item;
        if (item is null || item.EntryKind != ContextMenuEntryKind.ShellExtension || item.IsWindows11ContextMenu || item.IsDeleted || !item.IsPresentInRegistry)
            return Fail("Only a present classic Shell Extension registration can be wrapped.");
        if (!Guid.TryParse(item.HandlerClsid, out var originalClsid) || !TryOpenTarget(item.BackendRegistryPath, item.HandlerClsid!, writable: false, out var target))
            return Fail("The selected shell-extension registration no longer resolves to the expected handler CLSID.");

        using (target)
        {
            if (!IsContextMenuHandlerPath(item.BackendRegistryPath) || IsProxyClsid(originalClsid, target.Scope, target.View))
                return Fail("The selected registration is not eligible for proxy wrapping.");
        }

        var title = string.IsNullOrWhiteSpace(request.MenuTitle) ? item.DisplayName : request.MenuTitle.Trim();
        if (title.Length > 260) return Fail("The submenu title is too long.");

        var proxyClsid = Guid.NewGuid();
        var deployment = DeployImmutableBinaries();
        try
        {
            using var writableTarget = OpenTarget(item.BackendRegistryPath, target.Scope, target.View, writable: true);
            RegisterProxy(proxyClsid, originalClsid, title, item, writableTarget.Scope, writableTarget.View, deployment);
            // Persist/read validation before touching the third-party registration.
            var persisted = ReadMetadata(proxyClsid, writableTarget.Scope, writableTarget.View, ExtractSid(item.BackendRegistryPath));
            if (persisted is null || persisted.OriginalHandlerClsid != originalClsid.ToString("B"))
                throw new InvalidOperationException("Proxy metadata validation failed.");

            writableTarget.Key.SetValue(null, proxyClsid.ToString("B"), RegistryValueKind.String);
            ShellChangeNotifier.NotifyAssociationsChanged();
            await _logger.LogAsync($"ShellProxyWrap: WrapperId={proxyClsid:B}; Original={originalClsid:B}; Target={item.BackendRegistryPath}; View={writableTarget.View}; Result=Success.", cancellationToken);
            return new PipeResponse { Success = true, Message = "The handler menu will now appear in a submenu.", ShellProxyWrapper = ToStatus(persisted, ShellProxyHealth.Healthy) };
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Warning, $"ShellProxyWrap: Target={item.BackendRegistryPath}; Result=Failure; Error={ex}", cancellationToken);
            return Fail($"The proxy wrapper was not created: {ex.Message}");
        }
    }

    public async Task<PipeResponse> RemoveAsync(ShellProxyWrapperRequest request, CancellationToken cancellationToken)
    {
        var item = request.Item;
        if (item is null || !Guid.TryParse(item.ShellProxyClsid ?? item.HandlerClsid, out var proxyClsid)) return Fail("The selected item is not a proxy wrapper.");
        if (!TryFindMetadata(proxyClsid, ExtractSid(item.BackendRegistryPath), out var metadata, out var scope, out var view)) return Fail("Persistent proxy metadata was not found.");
        try
        {
            using var target = OpenTarget(metadata.TargetBackendRegistryPath, scope, view, writable: true);
            var current = target.Key.GetValue(null)?.ToString();
            if (string.Equals(current, proxyClsid.ToString("B"), StringComparison.OrdinalIgnoreCase))
            {
                target.Key.SetValue(null, metadata.OriginalHandlerClsid, RegistryValueKind.String);
                ShellChangeNotifier.NotifyAssociationsChanged();
            }
            if (string.Equals(target.Key.GetValue(null)?.ToString(), proxyClsid.ToString("B"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The original handler could not be restored.");
            DeleteProxyRegistration(proxyClsid, scope, view, metadata.OwnerSid);
            await _logger.LogAsync($"ShellProxyUnwrap: WrapperId={proxyClsid:B}; Target={metadata.TargetBackendRegistryPath}; Result=Success.", cancellationToken);
            return new PipeResponse { Success = true, Message = "The original top-level handler registration was restored." };
        }
        catch (Exception ex)
        {
            await _logger.LogAsync(RuntimeLogLevel.Warning, $"ShellProxyUnwrap: WrapperId={proxyClsid:B}; Result=Failure; Error={ex}", cancellationToken);
            return Fail($"The original registration was not restored: {ex.Message}");
        }
    }

    public PipeResponse GetStatus(ShellProxyWrapperRequest request)
    {
        var item = request.Item;
        if (item is null) return Fail("A menu item is required.");
        var proxy = item.ShellProxyClsid ?? item.HandlerClsid;
        if (!Guid.TryParse(proxy, out var clsid) || !TryFindMetadata(clsid, ExtractSid(item.BackendRegistryPath), out var metadata, out _, out _))
            return new PipeResponse { Success = true, Message = "The item is not wrapped.", ShellProxyWrapper = new ShellProxyWrapperStatus() };
        return new PipeResponse { Success = true, Message = "Proxy wrapper status loaded.", ShellProxyWrapper = ToStatus(metadata, DetermineHealth(metadata, clsid)) };
    }

    public PipeResponse UpdateAsync(ShellProxyWrapperRequest request)
    {
        var item = request.Item;
        if (item is null || string.IsNullOrWhiteSpace(request.MenuTitle) || !Guid.TryParse(item.ShellProxyClsid ?? item.HandlerClsid, out var proxy)) return Fail("A proxy wrapper and submenu title are required.");
        if (!TryFindMetadata(proxy, ExtractSid(item.BackendRegistryPath), out var metadata, out var scope, out var view)) return Fail("Persistent proxy metadata was not found.");
        using var key = OpenProxyKey(proxy, scope, view, writable: true, metadata.OwnerSid);
        using var details = key.CreateSubKey(MetadataKeyName, writable: true)!;
        details.SetValue("MenuTitle", request.MenuTitle.Trim(), RegistryValueKind.String);
        details.SetValue("UpdatedAtUtc", DateTimeOffset.UtcNow.ToString("O"), RegistryValueKind.String);
        metadata = metadata with { MenuTitle = request.MenuTitle.Trim() };
        return new PipeResponse { Success = true, Message = "Submenu title updated.", ShellProxyWrapper = ToStatus(metadata, DetermineHealth(metadata, proxy)) };
    }

    /// <summary>Reconciles exact durable targets before normal monitor classification.</summary>
    public async Task<bool> ReconcileAsync(BackendUserContext? userContext, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var candidate in EnumerateMetadata(userContext))
        {
            try
            {
                using var target = OpenTarget(candidate.Metadata.TargetBackendRegistryPath, candidate.Scope, candidate.View, writable: true);
                var current = target.Key.GetValue(null)?.ToString();
                var proxy = candidate.Metadata.ProxyClsid;
                if (string.Equals(current, proxy, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateDiagnosticPath(candidate.Metadata, candidate.Scope, candidate.View);
                    continue;
                }
                if (!Guid.TryParse(current, out var newOriginal) || IsProxyClsid(newOriginal, candidate.Scope, candidate.View) || !CanResolveComServer(newOriginal, candidate.Scope, candidate.View, candidate.Metadata.OwnerSid))
                {
                    await _logger.LogAsync(RuntimeLogLevel.Warning, $"ShellProxyReconciliationAmbiguous: WrapperId={proxy}; Target={candidate.Metadata.TargetBackendRegistryPath}; Current={current}; Result=Skipped.", cancellationToken);
                    continue;
                }
                var old = candidate.Metadata.OriginalHandlerClsid;
                var updated = candidate.Metadata with { OriginalHandlerClsid = newOriginal.ToString("B"), UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") };
                WriteMetadata(updated, candidate.Scope, candidate.View);
                if (ReadMetadata(Guid.Parse(updated.ProxyClsid), candidate.Scope, candidate.View, candidate.Metadata.OwnerSid)?.OriginalHandlerClsid != updated.OriginalHandlerClsid)
                    continue; // Never hide a third-party replacement if persistence failed.
                target.Key.SetValue(null, proxy, RegistryValueKind.String);
                UpdateDiagnosticPath(updated, candidate.Scope, candidate.View);
                changed = true;
                await _logger.LogAsync($"ShellProxyReconciliation: WrapperId={proxy}; OldOriginalClsid={old}; NewOriginalClsid={updated.OriginalHandlerClsid}; TargetPath={updated.TargetBackendRegistryPath}; RegistryView={candidate.View}; Action=RestoreProxy; Reason=ExactTargetChanged; Result=Success.", cancellationToken);
            }
            catch (Exception ex)
            {
                await _logger.LogAsync(RuntimeLogLevel.Warning, $"ShellProxyReconciliation: WrapperId={candidate.Metadata.ProxyClsid}; Target={candidate.Metadata.TargetBackendRegistryPath}; Result=Failure; Error={ex.Message}", cancellationToken);
            }
        }
        if (changed) ShellChangeNotifier.NotifyAssociationsChanged();
        return changed;
    }

    internal static bool TryProject(string proxyClsid, string targetPath, out ShellProxyMetadata metadata, out ShellProxyHealth health)
    {
        metadata = null!; health = ShellProxyHealth.Unknown;
        if (!Guid.TryParse(proxyClsid, out var proxy)) return false;
        var (_, _, sid) = ParsePath(targetPath);
        if (!TryFindMetadata(proxy, sid, out metadata, out _, out _)) return false;
        if (!string.Equals(metadata.TargetBackendRegistryPath, targetPath, StringComparison.OrdinalIgnoreCase)) return false;
        health = DetermineHealth(metadata, proxy);
        return true;
    }

    private static PipeResponse Fail(string message) => new() { Success = false, Message = message };
    private static ShellProxyWrapperStatus ToStatus(ShellProxyMetadata metadata, ShellProxyHealth health) => new() { IsWrapped = true, ProxyClsid = metadata.ProxyClsid, OriginalHandlerClsid = metadata.OriginalHandlerClsid, MenuTitle = metadata.MenuTitle, Health = health.ToString() };
    private static ShellProxyHealth DetermineHealth(ShellProxyMetadata metadata, Guid proxy)
    {
        try { using var target = OpenTarget(metadata.TargetBackendRegistryPath, ParseScope(metadata.Scope), ParseView(metadata.RegistryView), false); return string.Equals(target.Key.GetValue(null)?.ToString(), proxy.ToString("B"), StringComparison.OrdinalIgnoreCase) ? ShellProxyHealth.Healthy : ShellProxyHealth.TargetRestored; }
        catch { return ShellProxyHealth.TargetMissing; }
    }

    private Deployment DeployImmutableBinaries()
    {
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arch in new[] { "x86", "x64", "arm64" })
        {
            var source = Path.Combine(AppContext.BaseDirectory, "ShellProxy", arch, ProxyFileName);
            if (!File.Exists(source) || !MatchesMachine(source, arch)) throw new InvalidOperationException($"Missing or invalid ShellProxy binary for {arch}: {source}");
            sources[arch] = source;
        }
        var buildId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sources["x64"]))).ToLowerInvariant()[..16];
        var dir = Path.Combine(RuntimePaths.ShellProxyBinaryRootDirectory, buildId);
        Directory.CreateDirectory(dir);
        HardenDirectory(RuntimePaths.ShellProxyRootDirectory);
        HardenDirectory(RuntimePaths.ShellProxyBinaryRootDirectory);
        foreach (var (arch, source) in sources)
        {
            var destinationDir = Path.Combine(dir, arch); Directory.CreateDirectory(destinationDir); HardenDirectory(destinationDir);
            var destination = Path.Combine(destinationDir, ProxyFileName);
            if (!File.Exists(destination)) File.Copy(source, destination, overwrite: false);
            if (!MatchesMachine(destination, arch)) throw new InvalidOperationException($"Deployed ShellProxy binary has the wrong machine type: {destination}");
            HardenFile(destination);
        }
        return new Deployment(buildId, dir);
    }

    private static void HardenDirectory(string path)
    {
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    private static void HardenFile(string path)
    {
        var security = new FileSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static bool MatchesMachine(string path, string arch)
    {
        using var stream = File.OpenRead(path); var header = new byte[0x40]; if (stream.Read(header) != header.Length || header[0] != 'M' || header[1] != 'Z') return false;
        var offset = BitConverter.ToInt32(header, 0x3c); stream.Position = offset; var pe = new byte[6]; if (stream.Read(pe) != pe.Length || pe[0] != 'P' || pe[1] != 'E') return false;
        var machine = BitConverter.ToUInt16(pe, 4); return (arch, machine) switch { ("x86", 0x14c) or ("x64", 0x8664) or ("arm64", 0xaa64) => true, _ => false };
    }

    private static bool IsContextMenuHandlerPath(string path) => path.Contains(@"\shellex\ContextMenuHandlers\", StringComparison.OrdinalIgnoreCase);
    private static bool CanResolveComServer(Guid clsid, RegistryScope scope, RegistryView view, string? sid = null) { using var root = OpenClassRoot(scope, view, false, sid); using var key = root.OpenSubKey($@"CLSID\{clsid:B}\InprocServer32", false); return key?.GetValue(null) is string path && !string.IsNullOrWhiteSpace(path); }
    private static bool IsProxyClsid(Guid clsid, RegistryScope scope, RegistryView view) => ReadMetadata(clsid, scope, view) is not null;

    private static void RegisterProxy(Guid proxy, Guid original, string title, ContextMenuEntry item, RegistryScope scope, RegistryView view, Deployment deployment)
    {
        using var key = OpenProxyKey(proxy, scope, view, true, ExtractSid(item.BackendRegistryPath));
        key.SetValue(null, Path.Combine(deployment.Directory, ArchitectureLabel(view), ProxyFileName), RegistryValueKind.String);
        key.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
        var metadata = new ShellProxyMetadata(SchemaVersion, proxy.ToString("B"), original.ToString("B"), title, item.BackendRegistryPath, item.SourceRootPath, item.Category.ToString(), item.KeyName, scope == RegistryScope.User ? ExtractSid(item.BackendRegistryPath) : null, view.ToString(), deployment.BuildId, ResolveComServerPath(original, scope, view), DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"), scope.ToString());
        WriteMetadata(metadata, scope, view);
    }

    private static string ArchitectureLabel(RegistryView view) => view == RegistryView.Registry32 ? "x86" : Environment.Is64BitOperatingSystem && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    private static string? ExtractSid(string path) { const string marker = @"HKEY_USERS\"; if (!path.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) return null; var rest = path[marker.Length..]; return rest[..rest.IndexOf('\\')]; }
    private static string? ResolveComServerPath(Guid clsid, RegistryScope scope, RegistryView view, string? sid = null) { using var root = OpenClassRoot(scope, view, false, sid); using var key = root.OpenSubKey($@"CLSID\{clsid:B}\InprocServer32", false); return key?.GetValue(null)?.ToString(); }
    private static void UpdateDiagnosticPath(ShellProxyMetadata metadata, RegistryScope scope, RegistryView view) { var path = ResolveComServerPath(Guid.Parse(metadata.OriginalHandlerClsid), scope, view, metadata.OwnerSid); if (string.Equals(path, metadata.LastResolvedHandlerPath, StringComparison.OrdinalIgnoreCase)) return; WriteMetadata(metadata with { LastResolvedHandlerPath = path, UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") }, scope, view); }

    private static IEnumerable<(ShellProxyMetadata Metadata, RegistryScope Scope, RegistryView View)> EnumerateMetadata(BackendUserContext? user)
    {
        foreach (var scope in new[] { RegistryScope.Machine, RegistryScope.User }) foreach (var view in Views())
        {
            if (scope == RegistryScope.User && string.IsNullOrWhiteSpace(user?.Sid)) continue;
            using var root = OpenClassRoot(scope, view, false, user?.Sid); using var clsids = root.OpenSubKey("CLSID", false); if (clsids is null) continue;
            foreach (var name in clsids.GetSubKeyNames()) if (Guid.TryParse(name, out var id)) { var metadata = ReadMetadata(id, scope, view, user?.Sid); if (metadata is not null) yield return (metadata, scope, view); }
        }
    }
    private static IEnumerable<RegistryView> Views() => Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 };
    private static bool TryFindMetadata(Guid proxy, string? userSid, out ShellProxyMetadata metadata, out RegistryScope scope, out RegistryView view) { foreach (var candidateScope in new[] { RegistryScope.Machine, RegistryScope.User }) foreach (var candidateView in Views()) { if (candidateScope == RegistryScope.User && string.IsNullOrWhiteSpace(userSid)) continue; var found = ReadMetadata(proxy, candidateScope, candidateView, userSid); if (found is not null) { metadata = found; scope = candidateScope; view = candidateView; return true; } } metadata = null!; scope = default; view = default; return false; }
    private static ShellProxyMetadata? ReadMetadata(Guid proxy, RegistryScope scope, RegistryView view, string? userSid = null) { try { using var key = OpenProxyKey(proxy, scope, view, false, userSid); using var metadata = key.OpenSubKey(MetadataKeyName, false); if (metadata is null) return null; return new ShellProxyMetadata(Convert.ToInt32(metadata.GetValue("SchemaVersion", 0)), metadata.GetValue("ProxyClsid")?.ToString() ?? proxy.ToString("B"), metadata.GetValue("OriginalHandlerClsid")?.ToString() ?? string.Empty, metadata.GetValue("MenuTitle")?.ToString() ?? string.Empty, metadata.GetValue("TargetBackendRegistryPath")?.ToString() ?? string.Empty, metadata.GetValue("SourceRootPath")?.ToString() ?? string.Empty, metadata.GetValue("Category")?.ToString() ?? string.Empty, metadata.GetValue("OriginalKeyName")?.ToString() ?? string.Empty, metadata.GetValue("OwnerSid")?.ToString(), metadata.GetValue("RegistryView")?.ToString() ?? view.ToString(), metadata.GetValue("ProxyBuildId")?.ToString() ?? string.Empty, metadata.GetValue("LastResolvedHandlerPath")?.ToString(), metadata.GetValue("CreatedAtUtc")?.ToString() ?? string.Empty, metadata.GetValue("UpdatedAtUtc")?.ToString() ?? string.Empty, metadata.GetValue("Scope")?.ToString() ?? scope.ToString()); } catch { return null; } }
    private static void WriteMetadata(ShellProxyMetadata m, RegistryScope scope, RegistryView view) { using var key = OpenProxyKey(Guid.Parse(m.ProxyClsid), scope, view, true, m.OwnerSid); using var details = key.CreateSubKey(MetadataKeyName, true)!; foreach (var pair in new Dictionary<string, string?> { ["SchemaVersion"] = m.SchemaVersion.ToString(), ["ProxyClsid"] = m.ProxyClsid, ["OriginalHandlerClsid"] = m.OriginalHandlerClsid, ["MenuTitle"] = m.MenuTitle, ["TargetBackendRegistryPath"] = m.TargetBackendRegistryPath, ["SourceRootPath"] = m.SourceRootPath, ["Category"] = m.Category, ["OriginalKeyName"] = m.OriginalKeyName, ["OwnerSid"] = m.OwnerSid, ["RegistryView"] = m.RegistryView, ["ProxyBuildId"] = m.ProxyBuildId, ["LastResolvedHandlerPath"] = m.LastResolvedHandlerPath, ["CreatedAtUtc"] = m.CreatedAtUtc, ["UpdatedAtUtc"] = m.UpdatedAtUtc, ["Scope"] = m.Scope }) if (pair.Value is not null) details.SetValue(pair.Key, pair.Value, RegistryValueKind.String); }
    private static void DeleteProxyRegistration(Guid proxy, RegistryScope scope, RegistryView view, string? sid = null) { using var root = OpenClassRoot(scope, view, true, sid); root.DeleteSubKeyTree($@"CLSID\{proxy:B}", false); }
    private static RegistryTarget OpenTarget(string path, RegistryScope scope, RegistryView view, bool writable) { var (actualScope, subPath, sid) = ParsePath(path); if (actualScope != scope) throw new InvalidOperationException("The wrapper target scope changed."); var root = OpenClassRoot(scope, view, writable, sid); var key = root.OpenSubKey(subPath, writable) ?? throw new InvalidOperationException("The wrapper target no longer exists."); root.Dispose(); return new RegistryTarget(key, scope, view); }
    private static bool TryOpenTarget(string path, string expected, bool writable, out RegistryTarget target) { foreach (var view in Views()) { try { var (scope, _, _) = ParsePath(path); var candidate = OpenTarget(path, scope, view, writable); if (string.Equals(candidate.Key.GetValue(null)?.ToString(), expected, StringComparison.OrdinalIgnoreCase)) { target = candidate; return true; } candidate.Dispose(); } catch { } } target = null!; return false; }
    private static (RegistryScope Scope, string SubPath, string? Sid) ParsePath(string path) { const string lm = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\"; const string users = @"HKEY_USERS\"; if (path.StartsWith(lm, StringComparison.OrdinalIgnoreCase)) return (RegistryScope.Machine, path[lm.Length..], null); if (path.StartsWith(users, StringComparison.OrdinalIgnoreCase)) { var rest = path[users.Length..]; var marker = @"\Software\Classes\"; var index = rest.IndexOf(marker, StringComparison.OrdinalIgnoreCase); if (index > 0) return (RegistryScope.User, rest[(index + marker.Length)..], rest[..index]); } throw new InvalidOperationException("Wrapper target is not an explicit HKLM or HKEY_USERS Classes path."); }
    private static RegistryScope ParseScope(string value) => Enum.TryParse<RegistryScope>(value, out var result) ? result : RegistryScope.Machine;
    private static RegistryView ParseView(string value) => Enum.TryParse<RegistryView>(value, out var result) ? result : RegistryView.Default;
    private static RegistryKey OpenClassRoot(RegistryScope scope, RegistryView view, bool writable, string? sid = null) { if (scope == RegistryScope.Machine) return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"SOFTWARE\Classes", writable) ?? throw new InvalidOperationException("Machine Classes is unavailable."); if (string.IsNullOrWhiteSpace(sid)) throw new InvalidOperationException("User proxy metadata requires an explicit frontend SID."); return RegistryKey.OpenBaseKey(RegistryHive.Users, view).OpenSubKey($@"{sid}\Software\Classes", writable) ?? throw new InvalidOperationException("The frontend user Classes hive is unavailable."); }
    private static RegistryKey OpenProxyKey(Guid proxy, RegistryScope scope, RegistryView view, bool writable, string? sid = null) { var root = OpenClassRoot(scope, view, writable, sid); try { return writable ? root.CreateSubKey($@"CLSID\{proxy:B}", true) ?? throw new InvalidOperationException("Unable to create proxy COM registration.") : root.OpenSubKey($@"CLSID\{proxy:B}", false) ?? throw new InvalidOperationException("Proxy COM registration is unavailable."); } finally { root.Dispose(); } }

    internal sealed record ShellProxyMetadata(int SchemaVersion, string ProxyClsid, string OriginalHandlerClsid, string MenuTitle, string TargetBackendRegistryPath, string SourceRootPath, string Category, string OriginalKeyName, string? OwnerSid, string RegistryView, string ProxyBuildId, string? LastResolvedHandlerPath, string CreatedAtUtc, string UpdatedAtUtc, string Scope);
    private sealed record Deployment(string BuildId, string Directory);
    private sealed class RegistryTarget(RegistryKey key, RegistryScope scope, RegistryView view) : IDisposable { public RegistryKey Key { get; } = key; public RegistryScope Scope { get; } = scope; public RegistryView View { get; } = view; public void Dispose() => Key.Dispose(); }
    private enum RegistryScope { Machine, User }
}
