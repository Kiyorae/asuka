using System.Collections.ObjectModel;

namespace Matcha.App;

internal static class CollectionSync
{
    // Keep existing rows and their selection/scroll state when new activity arrives.
    public static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var retained = desired.ToHashSet();
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!retained.Contains(target[index])) target.RemoveAt(index);
        }

        var existingItems = target.ToHashSet();
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < target.Count && ReferenceEquals(target[index], desired[index])) continue;
            var existing = existingItems.Contains(desired[index]) ? target.IndexOf(desired[index]) : -1;
            if (existing >= 0) target.Move(existing, index);
            else target.Insert(index, desired[index]);
        }
    }
}
