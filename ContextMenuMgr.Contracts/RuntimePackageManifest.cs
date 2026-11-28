using System.Text.Json;

namespace ContextMenuMgr.Contracts;

public static class RuntimePackageManifest
{
    public const string FileName = "ContextMenuMgr.package.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RuntimePackageInfo Current { get; } = RuntimeHosting.IsInProcess
        ? new RuntimePackageInfo(RuntimePackageKind.Portable)
        : Read(AppContext.BaseDirectory);

    public static RuntimePackageInfo Read(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return RuntimePackageInfo.Installer;
        }

        try
        {
            var path = Path.Combine(baseDirectory, FileName);
            if (!File.Exists(path))
            {
                return RuntimePackageInfo.Installer;
            }

            using var stream = File.OpenRead(path);
            var manifest = JsonSerializer.Deserialize<PackageManifestModel>(stream, JsonOptions);
            return Enum.TryParse<RuntimePackageKind>(manifest?.PackageKind, ignoreCase: true, out var packageKind)
                ? new RuntimePackageInfo(packageKind)
                : RuntimePackageInfo.Installer;
        }
        catch
        {
            return RuntimePackageInfo.Installer;
        }
    }

    private sealed record PackageManifestModel(string? PackageKind);
}
