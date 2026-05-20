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

    /// <summary>
    /// When <c>true</c> (the default), syntax highlighting libraries (CodeMirror and highlight.js)
    /// are loaded from the Cloudflare CDN. All CDN assets are protected by Subresource Integrity (SRI)
    /// hashes so any tampering causes the browser to reject the resource.
    /// Set to <c>false</c> in air-gapped environments, under strict CSP policies, or where
    /// external network requests must be avoided. The profiler remains fully functional
    /// without highlighting; the query editor falls back to a plain textarea.
    /// </summary>
    public bool EnableSyntaxHighlighting { get; init; } = true;

    /// <summary>
    /// Command timeout in seconds for ad-hoc queries executed via the Profiler query editor.
    /// Applies to both <c>ExecuteQueryAsync</c> and <c>ExplainQueryAsync</c>.
    /// Defaults to 30 seconds; set higher for complex analytical queries.
    /// </summary>
    public int QueryTimeoutSeconds { get; init; } = 30;
}
