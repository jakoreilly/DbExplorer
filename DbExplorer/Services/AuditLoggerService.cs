using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Options;
using Microsoft.Extensions.Options;

namespace DbExplorer.Services;

/// <summary>
/// Writes structured audit events to the ILogger pipeline under the
/// <c>DbExplorer.Audit</c> category. When <c>Audit:Enabled</c> is false,
/// all calls are no-ops with zero overhead.
///
/// Route the "DbExplorer.Audit" category to a dedicated sink in your Serilog
/// (or other) configuration to keep audit logs separate from application logs.
///
/// GDPR note: this service logs WHO accessed WHAT and WHEN — it never logs
/// actual row data. SQL statements from ad-hoc queries and MCP tool calls are
/// included because they are operational metadata, not personal data; review
/// your data classification policy before enabling if users can embed PII in
/// query predicates.
/// </summary>
public sealed class AuditLoggerService : IAuditLogger
{
    private readonly bool _enabled;
    private readonly bool _logSql;
    private readonly ILogger<AuditLoggerService> _logger;

    // Pre-defined EventId values so log routing tools can filter by ID.
    private static readonly EventId MetadataAccessEvent = new(1001, "MetadataAccess");
    private static readonly EventId DataAccessEvent     = new(1002, "DataAccess");
    private static readonly EventId AdHocQueryEvent     = new(1003, "AdHocQuery");
    private static readonly EventId McpToolCallEvent    = new(1004, "McpToolCall");
    private static readonly EventId LoginEvent          = new(1005, "Login");
    private static readonly EventId LoginFailedEvent    = new(1006, "LoginFailed");
    private static readonly EventId LogoutEvent         = new(1007, "Logout");

    public AuditLoggerService(
        IOptions<AuditOptions> options,
        ILogger<AuditLoggerService> logger)
    {
        _enabled = options.Value.Enabled;
        _logSql  = options.Value.LogSql;
        _logger  = logger;
    }

    /// <inheritdoc/>
    public void Log(AuditEvent evt)
    {
        if (!_enabled) return;
        try
        {
            // Suppress SQL if the operator has disabled SQL capture (e.g. to avoid logging PII
            // that users might embed in query predicates).
            var sql = evt.Sql is null ? "-" : (_logSql ? evt.Sql : "<redacted>");

            var eventId = evt.Action switch
            {
                AuditAction.MetadataAccess => MetadataAccessEvent,
                AuditAction.DataAccess     => DataAccessEvent,
                AuditAction.AdHocQuery     => AdHocQueryEvent,
                AuditAction.McpToolCall    => McpToolCallEvent,
                AuditAction.Login          => LoginEvent,
                AuditAction.LoginFailed    => LoginFailedEvent,
                AuditAction.Logout         => LogoutEvent,
                _                          => new EventId(1000, "AuditEvent"),
            };

            // Use structured logging so sinks (e.g. Serilog JSON) capture each field
            // as a separate searchable property rather than a flat string.
            _logger.LogInformation(
                eventId,
                "AUDIT {Action} | user={Username} | schema={SchemaName} | object={ObjectName} | " +
                "rows={RowCount} | ms={ElapsedMs} | context={@Context} | sql={Sql}",
                evt.Action,
                evt.Username,
                evt.SchemaName ?? "-",
                evt.ObjectName ?? "-",
                evt.RowCount,
                evt.ElapsedMs,
                evt.Context,
                sql);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audit log write failed for action {Action}", evt.Action);
        }
    }
}
