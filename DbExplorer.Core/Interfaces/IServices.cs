using DbExplorer.Core.Models;

namespace DbExplorer.Core.Interfaces;

/// <summary>
/// Records a structured audit event for a data access action.
/// Implementations must never log actual row data — only access metadata.
/// </summary>
public interface IAuditLogger
{
    /// <summary>
    /// Logs a single access event. Safe to call fire-and-forget from any thread.
    /// </summary>
    void Log(AuditEvent evt);
}

public interface IIdentifierValidator
{
    /// <summary>
    /// Returns true if the schema name exists in the live catalog.
    /// </summary>
    Task<bool> SchemaExistsAsync(string schemaName, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the object (table/view/proc/function) exists in the given schema.
    /// </summary>
    Task<bool> ObjectExistsAsync(string schemaName, string objectName, CancellationToken ct = default);

    /// <summary>
    /// Validates a schema+object pair and throws InvalidOperationException if not found.
    /// </summary>
    Task ValidateObjectAsync(string schemaName, string objectName, CancellationToken ct = default);
}

public interface IMetadataService
{
    Task<string> GetCurrentCatalogAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CatalogInfo>> GetCatalogsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DatabaseObjectInfo>> GetObjectsAsync(string? schemaName = null, string? search = null, CancellationToken ct = default);
    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(string schemaName, string objectName, CancellationToken ct = default);
    Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(string schemaName, string tableName, CancellationToken ct = default);
    Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(string schemaName, string tableName, CancellationToken ct = default);

    /// <summary>
    /// Returns every foreign key whose parent table lives in <paramref name="schemaName"/>,
    /// in a single round-trip (used by the schema diagram).
    /// </summary>
    Task<IReadOnlyList<ForeignKeyInfo>> GetAllForeignKeysAsync(string schemaName, CancellationToken ct = default);
    Task<IReadOnlyList<TriggerInfo>> GetTriggersAsync(string schemaName, string tableName, CancellationToken ct = default);
    Task<ObjectDefinition?> GetObjectDefinitionAsync(string schemaName, string objectName, CancellationToken ct = default);
    Task<long> GetRowCountAsync(string schemaName, string tableName, CancellationToken ct = default);

    /// <summary>
    /// Searches object names and column names (with their owning table) across all schemas.
    /// Both the term and identifiers stay parameterized/catalog-validated per the usual pattern.
    /// </summary>
    Task<SearchResult> SearchAsync(string term, CancellationToken ct = default);

    /// <summary>
    /// Returns every column of every table/view in the current catalog in a single
    /// round-trip. Used to feed SQL editor autocomplete (table and column suggestions).
    /// </summary>
    Task<IReadOnlyList<ColumnSearchHit>> GetAllColumnsAsync(CancellationToken ct = default);
}

public interface IDataBrowsingService
{
    Task<PagedResult<DataRow>> GetPagedDataAsync(
        string schemaName,
        string objectName,
        PagingOptions paging,
        CancellationToken ct = default);

    /// <summary>Primary key columns for a table, in key order. Empty for tables/views without a PK.</summary>
    Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(
        string schemaName,
        string objectName,
        CancellationToken ct = default);

    /// <summary>
    /// Instant column intelligence (row/non-null/distinct counts, min/max) for the stats popover.
    /// When <paramref name="filters"/> is supplied, stats reflect the same WHERE clause as the grid.
    /// </summary>
    Task<ColumnStats> GetColumnStatsAsync(
        string schemaName,
        string objectName,
        string columnName,
        IReadOnlyList<ColumnFilter>? filters = null,
        CancellationToken ct = default);
}

public interface IAdHocQueryService
{
    Task<QueryResult> ExecuteQueryAsync(string sql, int maxRows = 1000, CancellationToken ct = default);
    Task<string> ExplainQueryAsync(string sql, CancellationToken ct = default);
    /// <summary>Returns a snapshot of all current sessions (including idle) on the server.</summary>
    Task<QueryResult> GetActivityAsync(CancellationToken ct = default);
    /// <summary>
    /// Returns recently executed queries from the database engine's statistics tables.
    /// Returns a result with a <see cref="QueryResult.Warning"/> message if the required
    /// feature (e.g. pg_stat_statements, performance_schema) is unavailable.
    /// </summary>
    Task<QueryResult> GetRecentQueriesAsync(CancellationToken ct = default);
}

public interface IPersistentQueryHistoryService
{
    Task AppendAsync(string username, ProfiledQuery entry, CancellationToken ct = default);
    Task<IReadOnlyList<ProfiledQuery>> GetForUserAsync(string username, int maxEntries = 200, CancellationToken ct = default);
    Task ClearAsync(string username, CancellationToken ct = default);
}

public interface IQueryProfiler
{
    void Record(string provider, string sql, long elapsedMs, int rowCount);
    IReadOnlyList<ProfiledQuery> GetHistory();
    void Clear();
}

public interface IQueryBuilderService
{
    /// <summary>
    /// Compiles a <see cref="QueryGraph"/> to provider-correct SQL using SqlKata.
    /// Returns the SQL string and the ordered list of parameter bindings.
    /// </summary>
    (string Sql, IReadOnlyList<object?> Bindings) Compile(QueryGraph graph);
}

/// <summary>
/// App-wide (singleton) rolling store of database actions and errors feeding
/// the Systems Analyser dashboard. Implementations must be thread-safe:
/// events arrive concurrently from every circuit and API request.
/// </summary>
public interface ISystemAnalyserStore
{
    /// <summary>Records one event. Never throws; safe to call fire-and-forget.</summary>
    void Record(DbActionEvent evt);

    /// <summary>Convenience for failure paths.</summary>
    void RecordError(
        string provider,
        DbActionCategory category,
        string operation,
        Exception ex,
        string? schemaName = null,
        string? objectName = null,
        long elapsedMs = -1,
        string username = "anonymous",
        string? sql = null);

    /// <summary>Newest-first snapshot of events no older than <paramref name="window"/>.</summary>
    IReadOnlyList<DbActionEvent> GetEvents(TimeSpan window);

    /// <summary>Removes all stored events.</summary>
    void Clear();

    /// <summary>Raised after each Record; fires on the recording thread (see Blazor thread rule).</summary>
    event Action? OnEvent;
}
