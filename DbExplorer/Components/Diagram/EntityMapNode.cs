using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using DbExplorer.Core.Models;

namespace DbExplorer.Components.Diagram;

/// <summary>
/// Read-only diagram node representing one table/view in the full-catalog Entity Map.
/// Unlike <see cref="TableDiagramNode"/> (built for the query builder's join canvas), this node
/// has no column checkboxes and only one link port per side — it is never a JOIN endpoint.
/// </summary>
public sealed class EntityMapNode : NodeModel
{
    public EntityMapNode(Point? position = null) : base(position ?? Point.Zero)
    {
        LeftPort = new PortModel(this, PortAlignment.Left);
        RightPort = new PortModel(this, PortAlignment.Right);
        AddPort(LeftPort);
        AddPort(RightPort);
    }

    public string SchemaName { get; set; } = "";
    public string TableName { get; set; } = "";

    /// <summary>Null until the user expands this node; then holds the loaded columns.</summary>
    public List<ColumnInfo>? Columns { get; set; }

    public bool IsExpanded { get; set; }
    public bool IsLoadingColumns { get; set; }

    /// <summary>Column names that participate in a foreign key (as child or as referenced key).</summary>
    public HashSet<string> FkColumns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int InDegree { get; set; }
    public int OutDegree { get; set; }

    /// <summary>True when this node matches the active search term or focus neighborhood.</summary>
    public bool Highlighted { get; set; }

    /// <summary>True when a search/focus is active and this node did not match.</summary>
    public bool Dimmed { get; set; }

    public PortModel LeftPort { get; }
    public PortModel RightPort { get; }

    public string QualifiedName => $"{SchemaName}.{TableName}";
}
