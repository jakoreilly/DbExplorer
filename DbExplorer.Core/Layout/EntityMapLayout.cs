namespace DbExplorer.Core.Layout;

public sealed record LayoutNode(string Id, double Width, double Height);

public sealed record LayoutEdge(string SourceId, string TargetId); // FK child -> referenced parent

public sealed record LayoutResult(IReadOnlyDictionary<string, (double X, double Y)> Positions);

/// <summary>
/// Deterministic layered layout for FK graphs. Layer 0 = tables referencing nothing
/// (parents/isolated); children flow left-to-right by FK direction. Within a layer,
/// nodes order by median index of previous-layer neighbors (one barycenter pass),
/// ties alphabetical for determinism.
/// </summary>
public static class EntityMapLayout
{
    public const double LayerGapX = 80;
    public const double NodeGapY = 30;

    public static LayoutResult Compute(IReadOnlyList<LayoutNode> nodes, IReadOnlyList<LayoutEdge> edges)
    {
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var outNb = nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var inNb = nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var e in edges.Where(e => byId.ContainsKey(e.SourceId) && byId.ContainsKey(e.TargetId))
                              .Where(e => !string.Equals(e.SourceId, e.TargetId, StringComparison.OrdinalIgnoreCase))
                              .DistinctBy(e => (e.SourceId.ToUpperInvariant(), e.TargetId.ToUpperInvariant())))
        {
            outNb[e.SourceId].Add(e.TargetId);
            inNb[e.TargetId].Add(e.SourceId);
        }

        // 1) Layer assignment: BFS from roots (nodes with no outgoing FK references).
        var layer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var roots = nodes.Where(n => outNb[n.Id].Count == 0).Select(n => n.Id)
                         .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        if (roots.Count == 0 && nodes.Count > 0) // fully cyclic: deterministic seed
            roots = [nodes.Select(n => n.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).First()];

        var queue = new Queue<string>(roots);
        foreach (var r in roots) layer[r] = 0;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var child in inNb[id].OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            {
                var proposed = layer[id] + 1;
                if (!layer.TryGetValue(child, out var cur))
                {
                    layer[child] = proposed;
                    queue.Enqueue(child);
                }
                else if (proposed > cur && proposed < nodes.Count) // relax; bound prevents cycle loops
                {
                    layer[child] = proposed;
                    queue.Enqueue(child);
                }
            }
        }
        foreach (var n in nodes.OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase))
            layer.TryAdd(n.Id, 0); // cycle-only islands

        // 2) Order within layers: single barycenter sweep by median neighbor index.
        var layers = layer.GroupBy(kv => kv.Value).OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList());
        for (var k = 1; layers.ContainsKey(k); k++)
        {
            var prevIndex = layers[k - 1].Select((id, i) => (id, i))
                .ToDictionary(t => t.id, t => t.i, StringComparer.OrdinalIgnoreCase);
            double Median(string id)
            {
                var idx = outNb[id].Concat(inNb[id])
                    .Where(prevIndex.ContainsKey).Select(n => (double)prevIndex[n])
                    .OrderBy(v => v).ToList();
                return idx.Count == 0 ? double.MaxValue : idx[idx.Count / 2];
            }
            layers[k] = layers[k].OrderBy(Median).ThenBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // 3) Coordinates: columns = layers; x advances by widest node per column.
        var positions = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);
        double x = 0;
        for (var k = 0; layers.ContainsKey(k); k++)
        {
            double y = 0, colWidth = 0;
            foreach (var id in layers[k])
            {
                var n = byId[id];
                positions[id] = (x, y);
                y += n.Height + NodeGapY;
                colWidth = Math.Max(colWidth, n.Width);
            }
            x += colWidth + LayerGapX;
        }
        return new LayoutResult(positions);
    }
}
