using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContextMenuMgr.Contracts;

// One settings file and one atomic writer shared with the elevated lifecycle
// transaction. This is not a second backend preferences database.
public static class FrontendSettingsFile
{
    public static void Write(string path, string json, bool preserveLifecycle = false)
    {
        using var lease = Acquire(path);
        if (preserveLifecycle)
        {
            using var stream = File.OpenRead(path);
            var policy = PortableServiceExitPolicy.Read(stream);
            var preferences = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Expected settings object.");
            preferences["autoStartOnLogin"] = policy.AutoStart;
            preferences["removePortableServiceOnExit"] = policy.RemoveOnExit;
            json = preferences.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        WriteCore(path, json);
    }

    public static void UpdateLifecycle(string path, bool autoStart, bool removeOnExit, PortableServiceExitPolicy? expected = null)
    {
        using var lease = Acquire(path);
        using (var stream = File.OpenRead(path))
        {
            var current = PortableServiceExitPolicy.Read(stream);
            if (expected is not null && (current.AutoStart != expected.AutoStart || current.RemoveOnExit != expected.RemoveOnExit))
                throw new IOException("Lifecycle settings changed externally; conditional update refused.");
        }
        var json = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new JsonException("Expected settings object.");
        foreach (var name in json.Select(p => p.Key).Where(n => n.Equals("autoStartOnLogin", StringComparison.OrdinalIgnoreCase)
            || n.Equals("removePortableServiceOnExit", StringComparison.OrdinalIgnoreCase)).ToArray()) json.Remove(name);
        json["autoStartOnLogin"] = autoStart;
        json["removePortableServiceOnExit"] = removeOnExit && !autoStart;
        WriteCore(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        using var verification = File.OpenRead(path);
        var actual = PortableServiceExitPolicy.Read(verification);
        if (actual.AutoStart != autoStart || actual.RemoveOnExit != (removeOnExit && !autoStart))
            throw new IOException("Lifecycle settings read-back mismatch.");
    }

    private static IDisposable Acquire(string path)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        var mutex = new Mutex(false, @"Global\ContextMenuMgr.Settings." + hash);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Settings file is busy.");
            return new MutexLease(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    private static void WriteCore(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }
}
