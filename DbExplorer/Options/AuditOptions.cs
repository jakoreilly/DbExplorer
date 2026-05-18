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

    /// <summary>
    /// When <c>true</c> (default), the SQL text of ad-hoc queries and MCP <c>RunSelectQuery</c>
    /// calls is included in the audit log. Set to <c>false</c> if users may embed personal data
    /// in query predicates (e.g. <c>WHERE email = 'user@example.com'</c>) and you want to
    /// exclude SQL from audit records.
    /// </summary>
    public bool LogSql { get; init; } = true;
}
