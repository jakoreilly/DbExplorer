using DbExplorer.Components.Diagram;

namespace DbExplorer.Services;

/// <summary>
/// Scoped service that bridges the diagram widget components (which Z.Blazor.Diagrams instantiates
/// without access to page parameters) back to the hosting page.
/// </summary>
public sealed class DiagramInteropService : IDisposable
{
    /// <summary>Called by <see cref="TableNodeWidget"/> when its delete button is clicked.</summary>
    public Action<TableDiagramNode>? OnNodeRemove { get; set; }

    /// <summary>Called by <see cref="TableNodeWidget"/> when column selection changes.</summary>
    public Action? OnGraphChanged { get; set; }

    /// <summary>Releases subscriber references to prevent memory leaks after circuit teardown.</summary>
    public void Dispose()
    {
        OnNodeRemove = null;
        OnGraphChanged = null;
    }
}
