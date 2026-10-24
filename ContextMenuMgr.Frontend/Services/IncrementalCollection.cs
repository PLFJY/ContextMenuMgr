using System.Collections.ObjectModel;

namespace ContextMenuMgr.Frontend.Services;

internal static class IncrementalCollection
{
    /// <summary>Preserves retained objects and emits only the necessary remove/insert/move notifications.</summary>
    public static void Apply<T>(ObservableCollection<T> current, IReadOnlyList<T> target) where T : class
    {
        var retained = target.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var index = current.Count - 1; index >= 0; index--)
            if (!retained.Contains(current[index])) current.RemoveAt(index);
        for (var index = 0; index < target.Count; index++)
        {
            if (index < current.Count && ReferenceEquals(current[index], target[index])) continue;
            var oldIndex = current.IndexOf(target[index]);
            if (oldIndex < 0) current.Insert(index, target[index]);
            else current.Move(oldIndex, index);
        }
    }
}
