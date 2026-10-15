namespace ContextMenuMgr.Backend.Services;

// Separate durable section: these are physical Applications registrations, never classic entries.
internal sealed class OpenWithMonitorState
{
    public Dictionary<string, OpenWithUserBaseline> Users { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, OpenWithProtectionRule> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class OpenWithUserBaseline
{
    public Dictionary<string, string> Registrations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Reviews { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class OpenWithProtectionRule
{
    public string Sid { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string CommandIdentity { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string ExecutableHash { get; set; } = string.Empty;
    public bool Authorized { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public string Outcome { get; set; } = "Satisfied";
    public DateTimeOffset LastVerifiedUtc { get; set; }
}
