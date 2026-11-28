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
    public Dictionary<string, string> Generations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ReviewVersions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, OpenWithVisibilityIntent> VisibilityIntents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MissingRegistrations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PendingIsolations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Quarantines { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

// Durable before-write journal. Recovery verifies; it never repeats a registry mutation.
internal sealed class OpenWithVisibilityIntent
{
    public string BeforeGeneration { get; set; } = string.Empty;
    public string CommandIdentity { get; set; } = string.Empty;
    public string ContentGeneration { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool ResolveReview { get; set; }
    public string? IsolationReviewStatus { get; set; }
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
