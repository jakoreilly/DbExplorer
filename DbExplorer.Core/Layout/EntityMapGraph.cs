namespace DbExplorer.Core.Layout;

/// <summary>
/// Pure BFS helpers over an FK adjacency list, used by the Entity Map's focus/degree-of-interest mode.
/// </summary>
public static class EntityMapGraph
{
    /// <summary>
    /// Returns <paramref name="rootId"/> plus every node reachable within <paramref name="hops"/>
    /// FK edges in either direction (child->parent or parent->child).
    /// </summary>
    public static HashSet<string> Neighborhood(string rootId, int hops, IReadOnlyList<LayoutEdge> edges)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in edges)
        {
            if (!adjacency.TryGetValue(e.SourceId, out var s)) adjacency[e.SourceId] = s = [];
            s.Add(e.TargetId);
            if (!adjacency.TryGetValue(e.TargetId, out var t)) adjacency[e.TargetId] = t = [];
            t.Add(e.SourceId);
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootId };
        if (hops <= 0) return visited;

        var frontier = new List<string> { rootId };
        for (var h = 0; h < hops && frontier.Count > 0; h++)
        {
            var next = new List<string>();
            foreach (var id in frontier)
            {
                if (!adjacency.TryGetValue(id, out var neighbors)) continue;
                foreach (var n in neighbors)
                {
                    if (visited.Add(n)) next.Add(n);
                }
            }
            frontier = next;
        }
        return visited;
    }
}
