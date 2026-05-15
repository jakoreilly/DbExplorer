namespace DbExplorer.Options;

/// <summary>
/// Feature flags for the visual query builder page.
/// </summary>
public sealed class QueryBuilderOptions
{
    /// <summary>
    /// When false the /query-builder route is disabled and the nav link is hidden.
    /// </summary>
    public bool Enabled { get; init; } = true;
}
