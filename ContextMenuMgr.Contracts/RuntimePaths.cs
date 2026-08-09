using System.IO;

namespace ContextMenuMgr.Contracts;

/// <summary>
/// Represents the runtime Paths.
/// </summary>
public static class RuntimePaths
{
    private static readonly string ProgramDataRootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ContextMenuMgr");

    private static readonly string PortableRootDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "Data");

    public static RuntimePackageKind PackageKind => RuntimePackageManifest.Current.PackageKind;

    /// <summary>
    /// Gets the root Directory.
    /// </summary>
    public static string RootDirectory { get; } = PackageKind == RuntimePackageKind.Portable
        ? PortableRootDirectory
        : ProgramDataRootDirectory;

    public static string LogsDirectory => Path.Combine(RootDirectory, "Logs");

    public static string BackendLogPath => Path.Combine(LogsDirectory, "backend.log");

    public static string FrontendDebugLogPath => Path.Combine(LogsDirectory, "frontend-debug.log");

    public static string FrontendCrashLogPath => Path.Combine(LogsDirectory, "frontend-crash.log");

    public static string TrayHostLogPath => Path.Combine(LogsDirectory, "trayhost.log");

    public static string SettingsPath => Path.Combine(RootDirectory, "frontend-settings.json");

    public static string StateDatabasePath => Path.Combine(RootDirectory, "context-menu-state.json");

    public static string DeletedBackupsDirectory => Path.Combine(RootDirectory, "DeletedBackups");

    public static string QuarantineDirectory => Path.Combine(RootDirectory, "Quarantine");

    public static string HostIdentityPath => Path.Combine(RootDirectory, "host-identity.json");

    public static string GetHostScopedDeletedBackupsDirectory(string hostPrefix)
        => Path.Combine(DeletedBackupsDirectory, GetSafeHostPrefix(hostPrefix));

    public static string DataDirectory => RootDirectory;

    public static string BackendProtectionSettingsPath => Path.Combine(DataDirectory, "backend-protection-settings.json");

    public static string GeneratedProgramsDirectory => Path.Combine(DataDirectory, "Programs");

    /// <summary>
    /// Gets the legacy Frontend Settings Path.
    /// </summary>
    public static string LegacyFrontendSettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ContextMenuMgr",
        "frontend-settings.json");

    public static string LegacyProgramDataRootDirectory => ProgramDataRootDirectory;

    public static string LegacyProgramDataSettingsPath => Path.Combine(
        LegacyProgramDataRootDirectory,
        "frontend-settings.json");

    public static string LegacyProgramDataStateDatabasePath => Path.Combine(
        LegacyProgramDataRootDirectory,
        "context-menu-state.json");

    public static string LegacyProgramDataBackendProtectionSettingsPath => Path.Combine(
        LegacyProgramDataRootDirectory,
        "backend-protection-settings.json");

    public static string LegacyProgramDataLogsDirectory => Path.Combine(
        LegacyProgramDataRootDirectory,
        "Logs");

    public static string LegacyProgramDataDeletedBackupsDirectory => Path.Combine(
        LegacyProgramDataRootDirectory,
        "DeletedBackups");

    public static string LegacyProgramDataGeneratedProgramsDirectory => Path.Combine(
        LegacyProgramDataRootDirectory,
        "Programs");

    public static string LegacyProgramDataDataDirectory => Path.Combine(
        LegacyProgramDataRootDirectory,
        "Data");

    /// <summary>
    /// Gets the legacy Frontend Logs Directory.
    /// </summary>
    public static string LegacyFrontendLogsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ContextMenuMgr",
        "Logs");

    /// <summary>
    /// Gets the legacy State Database Path.
    /// </summary>
    public static string LegacyStateDatabasePath { get; } = Path.Combine(
        LegacyProgramDataDataDirectory,
        "context-menu-state.json");

    /// <summary>
    /// Gets the legacy Backend Protection Settings Path.
    /// </summary>
    public static string LegacyBackendProtectionSettingsPath { get; } = Path.Combine(
        LegacyProgramDataDataDirectory,
        "backend-protection-settings.json");

    private static string GetSafeHostPrefix(string hostPrefix)
    {
        if (string.IsNullOrWhiteSpace(hostPrefix))
        {
            return "unknown-host";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var safeChars = hostPrefix
            .Where(ch => !invalidChars.Contains(ch))
            .ToArray();
        return safeChars.Length == 0 ? "unknown-host" : new string(safeChars);
    }
}
