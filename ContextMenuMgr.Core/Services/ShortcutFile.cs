using System.IO;
using System.Runtime.InteropServices;

namespace ContextMenuMgr.Backend.Services;

internal sealed record ShortcutInfo(
    string TargetPath,
    string Arguments,
    string WorkingDirectory,
    string Description,
    string IconLocation,
    bool RunAsAdministrator);

internal static class ShortcutFile
{
    private const int RunAsAdministratorFlagOffset = 0x15;
    private const byte RunAsAdministratorFlag = 0x20;

    public static ShortcutInfo Read(string path)
    {
        dynamic shell = CreateShell();
        dynamic shortcut = shell.CreateShortcut(path);
        var iconLocation = (string?)shortcut.IconLocation ?? string.Empty;
        return new ShortcutInfo(
            shortcut.TargetPath ?? string.Empty,
            shortcut.Arguments ?? string.Empty,
            shortcut.WorkingDirectory ?? string.Empty,
            shortcut.Description ?? string.Empty,
            iconLocation,
            IsRunAsAdministrator(path));
    }

    public static void Write(
        string path,
        string targetPath,
        string? arguments,
        string? workingDirectory,
        string? description,
        string? iconLocation,
        bool? runAsAdministrator)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        dynamic shell = CreateShell();
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = targetPath;
        shortcut.Arguments = arguments ?? string.Empty;
        shortcut.WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetDirectoryName(targetPath) ?? string.Empty
            : workingDirectory;
        shortcut.Description = description ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(iconLocation))
        {
            shortcut.IconLocation = iconLocation;
        }

        shortcut.Save();

        if (runAsAdministrator is not null)
        {
            SetRunAsAdministrator(path, runAsAdministrator.Value);
        }
    }

    public static void Update(
        string path,
        string? targetPath = null,
        string? arguments = null,
        string? workingDirectory = null,
        string? description = null,
        string? iconLocation = null,
        bool? runAsAdministrator = null)
    {
        dynamic shell = CreateShell();
        dynamic shortcut = shell.CreateShortcut(path);
        if (targetPath is not null)
        {
            shortcut.TargetPath = targetPath;
        }

        if (arguments is not null)
        {
            shortcut.Arguments = arguments;
        }

        if (workingDirectory is not null)
        {
            shortcut.WorkingDirectory = workingDirectory;
        }

        if (description is not null)
        {
            shortcut.Description = description;
        }

        if (iconLocation is not null)
        {
            shortcut.IconLocation = iconLocation;
        }

        shortcut.Save();

        if (runAsAdministrator is not null)
        {
            SetRunAsAdministrator(path, runAsAdministrator.Value);
        }
    }

    public static bool IsRunAsAdministrator(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes.Length > RunAsAdministratorFlagOffset
               && (bytes[RunAsAdministratorFlagOffset] & RunAsAdministratorFlag) == RunAsAdministratorFlag;
    }

    public static void SetRunAsAdministrator(string path, bool enabled)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length <= RunAsAdministratorFlagOffset)
        {
            return;
        }

        if (enabled)
        {
            bytes[RunAsAdministratorFlagOffset] |= RunAsAdministratorFlag;
        }
        else
        {
            bytes[RunAsAdministratorFlagOffset] &= unchecked((byte)~RunAsAdministratorFlag);
        }

        File.WriteAllBytes(path, bytes);
    }

    private static object CreateShell()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not available.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Unable to create WScript.Shell.");
    }
}
