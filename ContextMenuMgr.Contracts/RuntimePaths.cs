using System.IO;

namespace ContextMenuMgr.Contracts;

public static class RuntimePaths
{
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

    public static string LegacyFrontendSettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ContextMenuMgr",
        "frontend-settings.json");

    public static string LegacyFrontendLogsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ContextMenuMgr",
        "Logs");

    public static string LegacyStateDatabasePath { get; } = Path.Combine(
        RootDirectory,
        "Data",
        "context-menu-state.json");

    public static string LegacyBackendProtectionSettingsPath { get; } = Path.Combine(
        RootDirectory,
        "Data",
        "backend-protection-settings.json");
}
