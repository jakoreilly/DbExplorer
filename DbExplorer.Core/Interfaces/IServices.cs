using DbExplorer.Core.Models;

namespace DbExplorer.Core.Interfaces;

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
    Task<IReadOnlyList<DatabaseObjectInfo>> GetObjectsAsync(string? schemaName = null, CancellationToken ct = default);
    Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(string schemaName, string objectName, CancellationToken ct = default);
    Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(string schemaName, string tableName, CancellationToken ct = default);
    Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(string schemaName, string tableName, CancellationToken ct = default);
    Task<IReadOnlyList<TriggerInfo>> GetTriggersAsync(string schemaName, string tableName, CancellationToken ct = default);
    Task<ObjectDefinition?> GetObjectDefinitionAsync(string schemaName, string objectName, CancellationToken ct = default);
}

public interface IDataBrowsingService
{
    Task<PagedResult<DataRow>> GetPagedDataAsync(
        string schemaName,
        string objectName,
        PagingOptions paging,
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
