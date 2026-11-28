namespace ContextMenuMgr.Backend.Services;

/// <summary>Request-local coverage, also captured by shared discovery caches.</summary>
internal sealed class MenuDiscoveryCoverage : IDisposable
{
    private static readonly AsyncLocal<MenuDiscoveryCoverage?> Current = new();
    private readonly MenuDiscoveryCoverage? _parent;
    private int _incomplete;
    private int _classicIncomplete;
    public bool ClassicComplete => Volatile.Read(ref _classicIncomplete) == 0;
    public bool Complete => Volatile.Read(ref _incomplete) == 0;
    public static bool IsClassicComplete => Current.Value?.ClassicComplete ?? true;
    public static bool IsComplete => Current.Value?.Complete ?? true;
    private MenuDiscoveryCoverage() { _parent = Current.Value; Current.Value = this; }
    public static MenuDiscoveryCoverage Begin() => new();
    public static void MarkIncomplete()
    {
        if (Current.Value is not { } scope) return;
        Interlocked.Exchange(ref scope._incomplete, 1);
        Interlocked.Exchange(ref scope._classicIncomplete, 1);
    }
    public static void MarkPackagedIncomplete()
    { if (Current.Value is { } scope) Interlocked.Exchange(ref scope._incomplete, 1); }
    public void Dispose()
    {
        if (!Complete && _parent is not null) Interlocked.Exchange(ref _parent._incomplete, 1);
        if (!ClassicComplete && _parent is not null) Interlocked.Exchange(ref _parent._classicIncomplete, 1);
        Current.Value = _parent;
    }
}
