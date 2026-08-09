using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>
/// Represents the icon Preview Service.
/// </summary>
public sealed class IconPreviewService
{
    private const string DefaultIconPath = "imageres.dll";
    private const int DefaultIconIndex = -2;
    private const int DefaultExeIconIndex = -15;

    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _backgroundLoadLimiter = new(initialCount: 4, maxCount: 4);

    /// <summary>
    /// Gets icon.
    /// </summary>
    public ImageSource? GetIcon(string? iconPath, int iconIndex, string? fallbackFilePath = null)
    {
        if (iconIndex == 0 && TryParseIconLocation(iconPath, out var parsedIconPath, out var parsedIconIndex))
        {
            iconPath = parsedIconPath;
            iconIndex = parsedIconIndex;
        }

        var hasExplicitIcon = !string.IsNullOrWhiteSpace(iconPath);
        var normalizedPath = hasExplicitIcon ? NormalizeIconPath(iconPath!) : DefaultIconPath;
        var normalizedIndex = hasExplicitIcon ? iconIndex : DefaultIconIndex;
        var normalizedFallbackFilePath = string.IsNullOrWhiteSpace(fallbackFilePath)
            ? string.Empty
            : NormalizeIconPath(fallbackFilePath);
        var cacheKey = $"{normalizedPath}|{normalizedIndex}|{normalizedFallbackFilePath}|{hasExplicitIcon}";
        return _cache.GetOrAdd(cacheKey, _ => LoadIcon(normalizedPath, normalizedIndex, normalizedFallbackFilePath, preferFallback: !hasExplicitIcon));
    }

    /// <summary>
    /// Gets an icon without blocking the UI thread on shell or file-system icon extraction.
    /// </summary>
    public async Task<ImageSource?> GetIconAsync(
        string? iconPath,
        int iconIndex,
        string? fallbackFilePath = null,
        CancellationToken cancellationToken = default)
    {
        await _backgroundLoadLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                    () => GetIcon(iconPath, iconIndex, fallbackFilePath),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _backgroundLoadLimiter.Release();
        }
    }

    private static ImageSource? LoadIcon(string iconPath, int iconIndex, string? fallbackFilePath, bool preferFallback = false)
    {
        if (preferFallback && !string.IsNullOrWhiteSpace(fallbackFilePath))
        {
            var fallbackIcon = TryLoadIcon(fallbackFilePath, 0, useShellFileInfoFirst: true);
            if (fallbackIcon is not null)
            {
                return fallbackIcon;
            }
        }

        var icon = TryLoadIcon(iconPath, iconIndex);
        if (icon is not null)
        {
            return icon;
        }

        if (!string.IsNullOrWhiteSpace(fallbackFilePath)
            && !string.Equals(iconPath, fallbackFilePath, StringComparison.OrdinalIgnoreCase))
        {
            icon = TryLoadIcon(fallbackFilePath, 0, useShellFileInfoFirst: true);
            if (icon is not null)
            {
                return icon;
            }
        }

        if (Path.GetExtension(iconPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            icon = TryLoadIcon(DefaultIconPath, DefaultExeIconIndex);
            if (icon is not null)
            {
                return icon;
            }
        }

        return TryLoadIcon(DefaultIconPath, DefaultIconIndex);
    }

    private static ImageSource? TryLoadIcon(string iconPath, int iconIndex, bool useShellFileInfoFirst = false)
    {
        try
        {
            var resolvedPath = ResolveIconFilePath(iconPath);
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                if (LooksLikeExtension(iconPath))
                {
                    return TryLoadShellIcon(iconPath, FILE_ATTRIBUTE_NORMAL, useFileAttributes: true);
                }

                return null;
            }

            if (useShellFileInfoFirst)
            {
                var shellIcon = TryLoadShellIcon(resolvedPath);
                if (shellIcon is not null)
                {
                    return shellIcon;
                }
            }

            var largeIcons = new nint[1];
            var smallIcons = new nint[1];
            nint libraryHandle = nint.Zero;
            nint shellInfoIconHandle = nint.Zero;

            // Context Menu Manager Plus handles icon index -1 as a special case because
            // ExtractIconEx cannot load that resource identifier directly.
            if (iconIndex == -1)
            {
                libraryHandle = LoadLibraryW(resolvedPath);
                if (libraryHandle != nint.Zero)
                {
                    smallIcons[0] = LoadImageW(
                        libraryHandle,
                        "#1",
                        1,
                        20,
                        20,
                        0);
                }
            }
            else
            {
                ExtractIconExW(resolvedPath, iconIndex, largeIcons, smallIcons, 1);
            }

            var iconHandle = smallIcons[0] != nint.Zero ? smallIcons[0] : largeIcons[0];

            if (iconHandle == nint.Zero)
            {
                shellInfoIconHandle = GetShellIconHandle(resolvedPath);
                iconHandle = shellInfoIconHandle;
            }

            if (iconHandle == nint.Zero)
            {
                var associatedIconSource = TryLoadAssociatedIcon(resolvedPath);
                if (associatedIconSource is not null)
                {
                    return associatedIconSource;
                }
            }

            if (iconHandle == nint.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    iconHandle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(20, 20));

                source.Freeze();
                return source;
            }
            finally
            {
                if (largeIcons[0] != nint.Zero)
                {
                    DestroyIcon(largeIcons[0]);
                }

                if (smallIcons[0] != nint.Zero && smallIcons[0] != largeIcons[0])
                {
                    DestroyIcon(smallIcons[0]);
                }

                if (libraryHandle != nint.Zero)
                {
                    FreeLibrary(libraryHandle);
                }

                if (shellInfoIconHandle != nint.Zero
                    && shellInfoIconHandle != largeIcons[0]
                    && shellInfoIconHandle != smallIcons[0])
                {
                    DestroyIcon(shellInfoIconHandle);
                }
            }
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryLoadShellIcon(string resolvedPath, uint fileAttributes = 0, bool useFileAttributes = false)
    {
        var iconHandle = GetShellIconHandle(resolvedPath, fileAttributes, useFileAttributes);
        if (iconHandle == nint.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                iconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(20, 20));
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    private static ImageSource? TryLoadAssociatedIcon(string resolvedPath)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(resolvedPath);
            if (icon is null)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(20, 20));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeIconPath(string iconPath)
    {
        var normalized = Environment.ExpandEnvironmentVariables(iconPath.Trim().Trim('"'));
        return normalized.StartsWith('@')
            ? normalized[1..].Trim()
            : normalized;
    }

    private static bool TryParseIconLocation(string? value, out string? iconPath, out int iconIndex)
    {
        iconPath = null;
        iconIndex = 0;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (expanded.StartsWith('@'))
        {
            expanded = expanded[1..].Trim();
        }

        var commaIndex = expanded.LastIndexOf(',');
        if (commaIndex > 0 && int.TryParse(expanded[(commaIndex + 1)..].Trim(), out var parsedIndex))
        {
            iconPath = expanded[..commaIndex].Trim().Trim('"');
            iconIndex = parsedIndex;
            return !string.IsNullOrWhiteSpace(iconPath);
        }

        iconPath = expanded;
        return !string.IsNullOrWhiteSpace(iconPath);
    }

    private static string? ResolveIconFilePath(string iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return null;
        }

        if (iconPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return iconPath;
        }

        if (File.Exists(iconPath))
        {
            return iconPath;
        }

        if (Path.IsPathRooted(iconPath))
        {
            return iconPath;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, iconPath),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", iconPath),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", iconPath),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), iconPath),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", Path.GetFileName(iconPath)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Defender", Path.GetFileName(iconPath))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static nint GetShellIconHandle(string path, uint fileAttributes = 0, bool useFileAttributes = false)
    {
        var info = new SHFILEINFO();
        var flags = SHGFI_ICON | SHGFI_SMALLICON;
        if (useFileAttributes)
        {
            flags |= SHGFI_USEFILEATTRIBUTES;
        }

        var result = SHGetFileInfoW(path, fileAttributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        return result == nint.Zero ? nint.Zero : info.hIcon;
    }

    private static bool LooksLikeExtension(string value) =>
        value.StartsWith(".", StringComparison.Ordinal) && value.Length > 1 && !value.Contains('\\') && !value.Contains('/');

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW", SetLastError = true)]
    private static extern uint ExtractIconExW(
        string lpszFile,
        int nIconIndex,
        nint[]? phiconLarge,
        nint[]? phiconSmall,
        uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint hLibModule);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(
        nint hInst,
        string name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SHGetFileInfoW")]
    private static extern nint SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
}
