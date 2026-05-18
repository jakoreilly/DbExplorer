namespace DbExplorer.Options;

/// <summary>
/// Controls GDPR-compliant audit logging of data access events.
/// Configure under the "Audit" key in appsettings.json.
/// </summary>
public sealed class AuditOptions
{
    /// <summary>
    /// When <c>true</c>, audit events (who accessed what, when) are written to the
    /// structured log pipeline under the <c>DbExplorer.Audit</c> logger category.
    /// No row data is ever logged — only access metadata.
    /// </summary>
    public bool Enabled { get; init; } = false;
}
