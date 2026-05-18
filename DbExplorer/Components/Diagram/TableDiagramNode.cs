using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using DbExplorer.Core.Models;

namespace DbExplorer.Components.Diagram;

/// <summary>
/// Diagram node that represents a database table/view on the visual canvas.
/// Each column gets its own <see cref="PortModel"/> so users can draw JOIN links
/// directly between specific column rows.
/// </summary>
public sealed class TableDiagramNode : NodeModel
{
    private readonly Dictionary<string, PortModel> _columnPorts = new();

    public TableDiagramNode(string alias, Point? position = null)
        : base(position ?? Point.Zero)
    {
        Alias = alias;
    }

    /// <summary>SQL alias used in the generated query, e.g. "t0", "t1".</summary>
    public string Alias { get; }

    public string SchemaName { get; set; } = "";
    public string TableName  { get; set; } = "";

    /// <summary>Columns loaded from <see cref="IMetadataService"/>.</summary>
    public List<ColumnInfo> Columns { get; private set; } = new();

    /// <summary>Columns included in SELECT (unchecked means excluded).</summary>
    public HashSet<string> SelectedColumns { get; set; } = new();

    /// <summary>True while the metadata service is fetching column information.</summary>
    public bool IsLoading { get; set; }

    /// <summary>
    /// Loads column metadata and creates one port per side (left + right) per column so users
    /// can drag a JOIN link from either side to any column on another table.
    /// </summary>
    public void SetColumns(List<ColumnInfo> cols)
    {
        Columns = cols;
        SelectedColumns = cols.Select(c => c.ColumnName).ToHashSet();

        _columnPorts.Clear();
        foreach (var col in cols)
        {
            // Create a port on each side so links can be drawn in either direction
            var leftPort  = new PortModel(this, PortAlignment.Left);
            var rightPort = new PortModel(this, PortAlignment.Right);
            AddPort(leftPort);
            AddPort(rightPort);
            _columnPorts[$"{col.ColumnName}:left"]  = leftPort;
            _columnPorts[$"{col.ColumnName}:right"] = rightPort;
        }
    }

    /// <summary>Returns the column name whose port matches <paramref name="port"/>, or null.</summary>
    public string? GetColumnForPort(PortModel port)
    {
        var key = _columnPorts.FirstOrDefault(kv => kv.Value == port).Key;
        if (key is null) return null;
        var colon = key.LastIndexOf(':');
        return colon >= 0 ? key[..colon] : key;
    }

    /// <summary>Returns the right-hand port for <paramref name="columnName"/>, or null if not found.</summary>
    public PortModel? GetPortForColumn(string columnName)
        => _columnPorts.TryGetValue($"{columnName}:right", out var p) ? p : null;

    /// <summary>Returns the left-hand port for <paramref name="columnName"/>, or null if not found.</summary>
    public PortModel? GetLeftPortForColumn(string columnName)
        => _columnPorts.TryGetValue($"{columnName}:left", out var p) ? p : null;
}

