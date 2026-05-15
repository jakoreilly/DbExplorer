using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using DbExplorer.Core.Models;

namespace DbExplorer.Components.Diagram;

/// <summary>
/// Diagram node that represents a database table/view on the visual canvas.
/// Stores the schema metadata needed to populate column checkboxes and to
/// feed <see cref="QueryTableNode"/> when building the <see cref="QueryGraph"/>.
/// </summary>
public sealed class TableDiagramNode : NodeModel
{
    public TableDiagramNode(string alias, Point? position = null)
        : base(position ?? Point.Zero)
    {
        Alias = alias;
        // Left port: receives incoming JOIN connections
        AddPort(PortAlignment.Left);
        // Right port: originates outgoing JOIN connections
        AddPort(PortAlignment.Right);
    }

    /// <summary>SQL alias used in the generated query, e.g. "t0", "t1".</summary>
    public string Alias { get; }

    public string SchemaName { get; set; } = "";
    public string TableName  { get; set; } = "";

    /// <summary>Columns loaded from <see cref="IMetadataService"/>.</summary>
    public List<ColumnInfo> Columns { get; set; } = new();

    /// <summary>Columns included in SELECT (unchecked means excluded).</summary>
    public HashSet<string> SelectedColumns { get; set; } = new();

    /// <summary>True while the metadata service is fetching column information.</summary>
    public bool IsLoading { get; set; }
}
