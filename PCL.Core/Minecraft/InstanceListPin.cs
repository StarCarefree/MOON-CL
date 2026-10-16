using System;
using System.Collections.Generic;

namespace PCL.Core.Minecraft;

/// <summary>Temporarily pins live local instances without changing saved cards or favorites.</summary>
public static class InstanceListPin
{
    public static List<T> Pin<T>(IList<T> items, Func<T, string?> path, IEnumerable<string>? priorityPaths)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (priorityPaths is not null)
        {
            foreach (var candidate in priorityPaths)
            {
                if (string.IsNullOrEmpty(candidate) || rank.ContainsKey(candidate)) continue;
                rank[candidate] = rank.Count;
            }
        }

        var indexed = new (T item, int index)[items.Count];
        for (var i = 0; i < items.Count; i++) indexed[i] = (items[i], i);
        Array.Sort(indexed, (left, right) =>
        {
            var leftPinned = rank.ContainsKey(path(left.item) ?? "");
            var rightPinned = rank.ContainsKey(path(right.item) ?? "");
            if (leftPinned != rightPinned) return leftPinned ? -1 : 1;
            if (leftPinned && rank.TryGetValue(path(left.item) ?? "", out var leftRank) &&
                rank.TryGetValue(path(right.item) ?? "", out var rightRank) && leftRank != rightRank)
                return leftRank.CompareTo(rightRank);
            return left.index.CompareTo(right.index);
        });
        var result = new List<T>(indexed.Length);
        foreach (var entry in indexed) result.Add(entry.item);
        return result;
    }
}
