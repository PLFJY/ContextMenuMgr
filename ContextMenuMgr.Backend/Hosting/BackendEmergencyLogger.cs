using System.Security.Principal;
using ContextMenuMgr.Contracts;

namespace ContextMenuMgr.Backend.Hosting;

internal static class BackendEmergencyLogger
{
    public static void Log(string message)
    {
        try
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {FormatMessage(message)}{Environment.NewLine}";
            RuntimeLogFiles.WriteEmergency("service-startup.log", line);
        }
        catch { }
    }

    public static void Log(Exception exception, string context)
        => Log($"{context}{Environment.NewLine}{exception}");

    private static string FormatMessage(string message)
        => $"PID={Environment.ProcessId}, BaseDirectory={AppContext.BaseDirectory}, UserInteractive={Environment.UserInteractive}, Identity={TryGetCurrentIdentityName()}. {message}";

    private static string TryGetCurrentIdentityName()
    {
        try
        {
            return WindowsIdentity.GetCurrent().Name;
        }
        catch (Exception ex)
        {
            return $"<unavailable:{ex.GetType().Name}>";
        }
    }
}
