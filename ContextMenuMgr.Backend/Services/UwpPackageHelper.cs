using Microsoft.Win32;

namespace ContextMenuMgr.Backend.Services;

internal static class UwpPackageHelper
{
    private const string PackageRegPath = @"PackagedCom\Package";
    private const string PackagesRegPath = @"Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PackageRepository\Packages";

    public static string? GetPackageName(string? uwpName)
    {
        if (string.IsNullOrWhiteSpace(uwpName))
        {
            return null;
        }

        using var packageKey = Registry.ClassesRoot.OpenSubKey(PackageRegPath, writable: false);
        if (packageKey is null)
        {
            return null;
        }

        foreach (var packageName in packageKey.GetSubKeyNames())
        {
            if (packageName.StartsWith(uwpName, StringComparison.OrdinalIgnoreCase))
            {
                return packageName;
            }
        }

        return null;
    }

    public static string? GetFilePath(string? uwpName, Guid guid)
    {
        var packageName = GetPackageName(uwpName);
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return null;
        }

        var regPath = $@"{PackageRegPath}\{packageName}\Class\{guid:B}";
        using var packagedClassKey = Registry.ClassesRoot.OpenSubKey(regPath, writable: false);
        if (packagedClassKey is null)
        {
            return null;
        }

        using var packageInfoKey = Registry.ClassesRoot.OpenSubKey($@"{PackagesRegPath}\{packageName}", writable: false);
        if (packageInfoKey is null)
        {
            return null;
        }

        var directoryPath = packageInfoKey.GetValue("Path")?.ToString();
        var dllPath = packagedClassKey.GetValue("DllPath")?.ToString();
        if (!string.IsNullOrWhiteSpace(directoryPath) && !string.IsNullOrWhiteSpace(dllPath))
        {
            var combinedFilePath = Path.Combine(directoryPath, dllPath);
            if (File.Exists(combinedFilePath))
            {
                return combinedFilePath;
            }
        }

        var serverKeyNames = packageInfoKey.GetSubKeyNames();
        if (serverKeyNames.Length == 1)
        {
            return $@"shell:AppsFolder\{serverKeyNames[0]}";
        }

        return !string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath)
            ? directoryPath
            : null;
    }
}
