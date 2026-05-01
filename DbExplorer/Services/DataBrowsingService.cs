using Dapper;
using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Options;
using Microsoft.Extensions.Options;

namespace DbExplorer.Services;

/// <summary>
/// Queries paged data from tables and views.
/// Identifiers are dialect-quoted after catalog validation; user values never enter SQL text.
/// </summary>
public sealed class DataBrowsingService(
    IDbConnectionFactory factory,
    SqlDialect dialect,
    IIdentifierValidator validator,
    ILogger<DataBrowsingService> logger,
    IOptions<DataBrowsingOptions> options) : IDataBrowsingService
{
    private readonly DataBrowsingOptions _options = options.Value;

    public async Task<PagedResult<DataRow>> GetPagedDataAsync(
        string schemaName,
        string objectName,
        PagingOptions paging,
        CancellationToken ct = default)
    {
        // 1. Validate format first (cheap)
        dialect.ThrowIfInvalidIdentifier(schemaName, nameof(schemaName));
        dialect.ThrowIfInvalidIdentifier(objectName, nameof(objectName));

        // 2. Validate against live catalog
        await validator.ValidateObjectAsync(schemaName, objectName, ct);

        // 3. Clamp page size; allow larger export requests up to MaxExportRows.
        var pageSize = paging.PageSize <= 0
            ? _options.DefaultPageSize
            : paging.PageSize > _options.MaxPageSize
                ? Math.Min(paging.PageSize, _options.MaxExportRows)
                : paging.PageSize;
        var pageNumber = Math.Max(1, paging.PageNumber);
        var offset = (pageNumber - 1) * pageSize;

        // 4. Quote validated identifiers for active dialect
        var fullName = dialect.QuoteQualifiedName(schemaName, objectName);

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var direction = paging.OrderByPrimaryKey == SortDirection.Ascending ? "ASC" : "DESC";
        var pkColumns = await GetPrimaryKeyColumnsAsync(conn, schemaName, objectName, ct);
        var orderByPk = pkColumns.Count > 0
            ? string.Join(", ", pkColumns.Select(c => $"{dialect.QuoteIdentifier(c)} {direction}"))
            : null;

        // Count
        var countSql = factory.Provider switch
        {
            DatabaseProvider.SqlServer => $"SELECT COUNT_BIG(*) FROM {fullName}",
            DatabaseProvider.PostgreSql or DatabaseProvider.MySql => $"SELECT COUNT(*) FROM {fullName}",
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var totalCount = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                countSql,
                commandTimeout: _options.QueryTimeoutSeconds,
                cancellationToken: ct));

        var dataSql = factory.Provider switch
        {
            DatabaseProvider.SqlServer => $"""
                SELECT *
                FROM {fullName}
                ORDER BY {(orderByPk ?? "(SELECT NULL)")}
                OFFSET @offset ROWS
                FETCH NEXT @pageSize ROWS ONLY
                """,
            DatabaseProvider.PostgreSql or DatabaseProvider.MySql => $"""
                SELECT *
                FROM {fullName}
                {(orderByPk is not null ? "ORDER BY " + orderByPk : "")}
                LIMIT @pageSize OFFSET @offset
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var rawRows = await conn.QueryAsync(
            new CommandDefinition(dataSql,
                new { offset, pageSize },
                commandTimeout: _options.QueryTimeoutSeconds,
                cancellationToken: ct));

        var dataRows = rawRows
            .Select(r =>
            {
                var dict = (IDictionary<string, object?>)(IDictionary<string, object>)r;
                // Dapper returns IDictionary<string,object>; cast safely
                return new DataRow(dict.ToDictionary(k => k.Key, k => (object?)k.Value));
            })
            .ToList();

        logger.LogInformation(
            "Paged data fetched from {Schema}.{Object}: page {Page}/{PageSize}, total {Total}",
            schemaName, objectName, pageNumber, pageSize, totalCount);

        return new PagedResult<DataRow>(dataRows, pageNumber, pageSize, totalCount);
    }

    private async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(
        System.Data.Common.DbConnection conn,
        string schemaName,
        string objectName,
        CancellationToken ct)
    {
        var sql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
                """
                SELECT c.name
                FROM sys.indexes i
                JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                JOIN sys.objects o ON i.object_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE i.is_primary_key = 1
                  AND s.name = @schema
                  AND o.name = @obj
                ORDER BY ic.key_ordinal
                """,
            DatabaseProvider.PostgreSql =>
                """
                SELECT a.attname
                FROM pg_index i
                JOIN pg_class t ON t.oid = i.indrelid
                JOIN pg_namespace n ON n.oid = t.relnamespace
                JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(i.indkey)
                WHERE i.indisprimary
                  AND n.nspname = @schema
                  AND t.relname = @obj
                ORDER BY array_position(i.indkey, a.attnum)
                """,
            DatabaseProvider.MySql =>
                """
                SELECT s.column_name
                FROM information_schema.statistics s
                WHERE s.table_schema = @schema
                  AND s.table_name = @obj
                  AND s.index_name = 'PRIMARY'
                ORDER BY s.seq_in_index
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var rows = await conn.QueryAsync<string>(
            new CommandDefinition(
                sql,
                new { schema = schemaName, obj = objectName },
                commandTimeout: _options.QueryTimeoutSeconds,
                cancellationToken: ct));

        return rows.ToList();
    }
}
