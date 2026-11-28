using System.IO;
using System.Text;

namespace ContextMenuMgr.Backend.Services;

internal static class DesktopIniStore
{
    private const string SectionName = "LocalizedFileNames";

    public static string GetLocalizedFileName(string path, bool translate = true)
    {
        try
        {
            var iniPath = GetDesktopIniPath(path);
            if (!File.Exists(iniPath))
            {
                return string.Empty;
            }

            var fileName = Path.GetFileName(path);
            var inSection = false;
            foreach (var line in File.ReadAllLines(iniPath, Encoding.Unicode))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    inSection = string.Equals(trimmed, $"[{SectionName}]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection)
                {
                    continue;
                }

                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                if (string.Equals(trimmed[..separator], fileName, StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed[(separator + 1)..];
                    return translate ? ShellMetadataResolver.ResolveResourceString(value) : value;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return string.Empty;
    }

    public static void SetLocalizedFileName(string path, string displayName)
    {
        try
        {
            var iniPath = GetDesktopIniPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(iniPath)!);

            var fileName = Path.GetFileName(path);
            var values = LoadValues(iniPath);
            values[fileName] = displayName;
            SaveValues(iniPath, values);

            File.SetAttributes(iniPath, File.GetAttributes(iniPath) | FileAttributes.Hidden | FileAttributes.System);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.ReadOnly);
            }
        }
        catch
        {
        }
    }

    public static void DeleteLocalizedFileName(string path)
    {
        try
        {
            var iniPath = GetDesktopIniPath(path);
            if (!File.Exists(iniPath))
            {
                return;
            }

            var values = LoadValues(iniPath);
            if (values.Remove(Path.GetFileName(path)))
            {
                SaveValues(iniPath, values);
            }
        }
        catch
        {
        }
    }

    private static string GetDesktopIniPath(string path) => Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "desktop.ini");

    private static Dictionary<string, string> LoadValues(string iniPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(iniPath))
        {
            return result;
        }

        var inSection = false;
        foreach (var line in File.ReadAllLines(iniPath, Encoding.Unicode))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inSection = string.Equals(trimmed, $"[{SectionName}]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator > 0)
            {
                result[trimmed[..separator]] = trimmed[(separator + 1)..];
            }
        }

        return result;
    }

    private static void SaveValues(string iniPath, IReadOnlyDictionary<string, string> values)
    {
        EnsureWritable(iniPath);
        var builder = new StringBuilder();
        builder.AppendLine($"[{SectionName}]");
        foreach (var pair in values.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"{pair.Key}={pair.Value}");
        }

        File.WriteAllText(iniPath, builder.ToString(), Encoding.Unicode);
    }

    private static void EnsureWritable(string iniPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(iniPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
            }

            if (File.Exists(iniPath))
            {
                File.SetAttributes(iniPath, File.GetAttributes(iniPath) & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
            }
        }
        catch
        {
        }
    }
}
