using System.Collections;
using System.Text.Json;

namespace ContextMenuMgr.Frontend.Services;

/// <summary>Retains discovery coverage across the pipe client boundary, including old servers.</summary>
public sealed class MenuListSnapshot<T>(IReadOnlyList<T> items, bool isComplete, bool isFromCache = false, Func<MenuListSnapshot<T>>? captureCurrent = null, Func<T, bool>? canRemove = null)
    : IReadOnlyList<T>
{
    public MenuListSnapshot<T> CaptureCurrent() => captureCurrent?.Invoke() ?? this;
    public bool IsComplete { get; } = isComplete;
    public bool CanRemove(T item) => canRemove?.Invoke(item) ?? IsComplete;
    public bool IsFromCache { get; } = isFromCache;
    public int Count => items.Count;
    public T this[int index] => items[index];
    public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class MenuListData
{
    // DTOs contain nested arrays/dictionaries whose record equality is reference based.
    public static bool Equivalent<T>(T left, T right)
        => EqualityComparer<T>.Default.Equals(left, right)
           || JsonSerializer.SerializeToUtf8Bytes(left).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right));

    public static IReadOnlyList<T> Current<T>(IReadOnlyList<T> snapshot)
        => snapshot is MenuListSnapshot<T> tracked ? tracked.CaptureCurrent() : snapshot;

    public static bool AllowsRemoval<T>(IReadOnlyList<T> snapshot)
        => snapshot is MenuListSnapshot<T> { IsComplete: true };
    public static bool AllowsRemoval<T>(IReadOnlyList<T> snapshot, T item)
        => snapshot is MenuListSnapshot<T> tracked && tracked.CanRemove(item);
}
