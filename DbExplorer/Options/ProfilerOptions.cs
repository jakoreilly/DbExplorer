namespace DbExplorer.Options;

/// <summary>
/// Controls which features of the Query Profiler page are enabled.
/// Configure under the "Profiler" key in appsettings.json.
/// </summary>
public sealed class ProfilerOptions
{
    /// <summary>
    /// When <c>true</c> (the default), the Query Editor panel is visible on the Profiler page,
    /// allowing authenticated users to run ad-hoc read-only SELECT statements.
    /// Set to <c>false</c> to hide the panel entirely for environments where query execution
    /// should be restricted (e.g. production read replicas where you want activity monitoring
    /// but not ad-hoc querying).
    /// </summary>
    public bool EnableQueryEditor { get; init; } = true;
}
