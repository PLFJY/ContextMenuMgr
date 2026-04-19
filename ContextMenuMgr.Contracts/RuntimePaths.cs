using System.IO;

namespace ContextMenuMgr.Contracts;

/// <summary>
/// Represents the runtime Paths.
/// </summary>
public static class RuntimePaths
{
    /// <summary>
    /// Gets the root Directory.
    /// </summary>
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ContextMenuMgr");

    public static string LogsDirectory => Path.Combine(RootDirectory, "Logs");

    public static string BackendLogPath => Path.Combine(LogsDirectory, "backend.log");

    public static string FrontendDebugLogPath => Path.Combine(LogsDirectory, "frontend-debug.log");

    public static string FrontendCrashLogPath => Path.Combine(LogsDirectory, "frontend-crash.log");

    public static string TrayHostLogPath => Path.Combine(LogsDirectory, "trayhost.log");

    public static string SettingsPath => Path.Combine(RootDirectory, "frontend-settings.json");

    public static string StateDatabasePath => Path.Combine(RootDirectory, "context-menu-state.json");

    public static string DeletedBackupsDirectory => Path.Combine(RootDirectory, "DeletedBackups");

    public static string DataDirectory => RootDirectory;

    /// <summary>
    /// Gets the legacy Frontend Settings Path.
    /// </summary>
    public static string LegacyFrontendSettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ContextMenuMgr",
        "frontend-settings.json");

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
        RootDirectory,
        "Data",
        "context-menu-state.json");

    /// <summary>
    /// Gets the legacy Backend Protection Settings Path.
    /// </summary>
    public static string LegacyBackendProtectionSettingsPath { get; } = Path.Combine(
        RootDirectory,
        "Data",
        "backend-protection-settings.json");
}
