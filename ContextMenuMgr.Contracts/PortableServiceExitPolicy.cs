using System.Text.Json;

namespace ContextMenuMgr.Contracts;

// Read only the lifecycle fields from the existing settings file. A readable
// legacy object defaults to cleanup enabled; missing/corrupt files never grant
// a privileged deletion. Installer callers must still reject this policy.
public sealed record PortableServiceExitPolicy(bool RemoveOnExit, bool AutoStart, bool KeepBackground)
{
    public static bool HasUnsettledStartupTransaction(string dataDirectory)
    {
        try { _ = File.GetAttributes(Path.Combine(dataDirectory, ServiceMetadata.PortableStartupTransactionMarkerFileName)); return true; }
        catch (FileNotFoundException) { return false; }
        catch { return true; }
    }

    public static PortableServiceExitPolicy Read(Stream stream)
    {
        if (!stream.CanSeek || stream.Length > 4 * 1024 * 1024)
            throw new IOException("Lifecycle settings are not a bounded local settings snapshot.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a settings object.");

        bool ReadBoolean(string name, bool fallback)
        {
            var matches = document.RootElement.EnumerateObject()
                .Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) return fallback;
            if (matches.Length != 1 || matches[0].Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException($"Invalid or duplicate lifecycle setting: {name}.");
            return matches[0].Value.GetBoolean();
        }

        return new(ReadBoolean("removePortableServiceOnExit", true),
            ReadBoolean("autoStartOnLogin", false), ReadBoolean("keepBackgroundAfterClose", false));
    }

    public bool AllowsRemoval(RuntimePackageKind packageKind, bool windowsShutdown)
        => packageKind == RuntimePackageKind.Portable && RemoveOnExit && !AutoStart
           && (windowsShutdown || !KeepBackground);
}
