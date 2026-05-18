namespace DbExplorer.Core.Models;

/// <summary>
/// The category of data access operation that triggered an audit event.
/// </summary>
public enum AuditAction
{
    /// <summary>User browsed object metadata (columns, indexes, foreign keys, definition).</summary>
    MetadataAccess,
    /// <summary>User browsed paged table/view data or exported as CSV.</summary>
    DataAccess,
    /// <summary>User ran an ad-hoc SELECT query via the Profiler.</summary>
    AdHocQuery,
    /// <summary>An MCP tool call accessed schema metadata or ran a query.</summary>
    McpToolCall,
}

/// <summary>
/// Immutable record of a single data access event for audit/GDPR purposes.
/// Row contents are never included.
/// </summary>
public sealed record AuditEvent(
    /// <summary>UTC timestamp of the event.</summary>
    DateTimeOffset Timestamp,
    /// <summary>Authenticated username, or "anonymous" if unavailable.</summary>
    string Username,
    /// <summary>Category of the access.</summary>
    AuditAction Action,
    /// <summary>Database schema involved, if applicable.</summary>
    string? SchemaName,
    /// <summary>Table/view/object name involved, if applicable.</summary>
    string? ObjectName,
    /// <summary>Number of rows returned or affected. -1 if not applicable.</summary>
    int RowCount,
    /// <summary>Elapsed time in milliseconds. -1 if not measured.</summary>
    long ElapsedMs,
    /// <summary>For ad-hoc queries and MCP tool calls: the SQL executed.</summary>
    string? Sql = null,
    /// <summary>For MCP tool calls: the tool name invoked.</summary>
    string? McpTool = null
);

public enum SortDirection
{
    Ascending = 0,
    Descending = 1
}

public record SchemaInfo(string SchemaName);

public record CatalogInfo(string CatalogName);

public record DatabaseObjectInfo(
    string SchemaName,
    string ObjectName,
    string ObjectType   // TABLE, VIEW, PROCEDURE, SCALAR_FUNCTION, TABLE_FUNCTION
);

public record ColumnInfo(
    string SchemaName,
    string TableName,
    string ColumnName,
    int OrdinalPosition,
    bool IsNullable,
    string DataType,
    int? MaxLength,
    int? NumericPrecision,
    int? NumericScale,
    bool IsPrimaryKey,
    string? DefaultValue
);

public record IndexInfo(
    string SchemaName,
    string TableName,
    string IndexName,
    bool IsUnique,
    bool IsPrimaryKey,
    bool IsClustered,
    string Columns
);

public record ForeignKeyInfo(
    string ConstraintName,
    string SchemaName,
    string TableName,
    string ColumnName,
    string ReferencedSchema,
    string ReferencedTable,
    string ReferencedColumn
);

public record ObjectDefinition(
    string SchemaName,
    string ObjectName,
    string ObjectType,
    string? Definition
);

public record TriggerInfo(
    string SchemaName,
    string TableName,
    string TriggerName,
    bool IsEnabled,
    string Events
);

public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount
);

public record DataRow(IDictionary<string, object?> Fields);

public record PagingOptions
{
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public SortDirection OrderByPrimaryKey { get; init; } = SortDirection.Descending;
    /// <summary>
    /// When set, the grid sorts by this column instead of the primary key.
    /// Validated and quoted by the service before use in SQL.
    /// </summary>
    public string? SortColumn { get; init; }
    public SortDirection SortColumnDirection { get; init; } = SortDirection.Ascending;
}

// ── Profiler models ───────────────────────────────────────────────────────────

public record QueryResultColumn(string Name);

public record QueryResult(
    IReadOnlyList<QueryResultColumn> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    long ElapsedMs,
    string? Warning
);

public record ProfiledQuery(
    DateTimeOffset Timestamp,
    string Provider,
    string Sql,
    long ElapsedMs,
    int RowCount
);
