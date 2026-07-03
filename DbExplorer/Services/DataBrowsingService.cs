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
    IQueryProfiler profiler,
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

        var hasFilters = paging.Filters is { Count: > 0 };
        HashSet<string>? catalogColumns = null;
        if (hasFilters || !string.IsNullOrWhiteSpace(paging.SortColumn))
            catalogColumns = await GetColumnNamesAsync(conn, schemaName, objectName, ct);

        string? orderByClause = orderByPk;
        if (!string.IsNullOrWhiteSpace(paging.SortColumn))
        {
            dialect.ThrowIfInvalidIdentifier(paging.SortColumn, nameof(paging.SortColumn));
            if (catalogColumns is not null && catalogColumns.Contains(paging.SortColumn))
            {
                var colDir = paging.SortColumnDirection == SortDirection.Ascending ? "ASC" : "DESC";
                orderByClause = $"{dialect.QuoteIdentifier(paging.SortColumn)} {colDir}";
            }
        }

        var args = new DynamicParameters();
        var whereClause = string.Empty;
        if (hasFilters)
        {
            var predicates = new List<string>();
            var i = 0;
            foreach (var filter in paging.Filters!)
            {
                if (string.IsNullOrWhiteSpace(filter.ColumnName) || catalogColumns is null || !catalogColumns.Contains(filter.ColumnName))
                    continue;

                dialect.ThrowIfInvalidIdentifier(filter.ColumnName, nameof(filter.ColumnName));
                var predicate = FilterSql.BuildPredicate(
                    dialect.QuoteIdentifier(filter.ColumnName), $"f{i++}", filter, args);
                if (predicate is not null)
                    predicates.Add(predicate);
            }

            if (predicates.Count > 0)
                whereClause = "WHERE " + string.Join(" AND ", predicates);
        }

        int totalCount = -1;
        if (_options.EagerRowCount)
        {
            var countSql = factory.Provider switch
            {
                DatabaseProvider.SqlServer => $"SELECT COUNT_BIG(*) FROM {fullName} {whereClause}",
                DatabaseProvider.PostgreSql or DatabaseProvider.MySql => $"SELECT COUNT(*) FROM {fullName} {whereClause}",
                _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
            };

            totalCount = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    countSql,
                    args,
                    commandTimeout: _options.QueryTimeoutSeconds,
                    cancellationToken: ct));
        }

        var dataSql = factory.Provider switch
        {
            DatabaseProvider.SqlServer => $"""
                SELECT *
                FROM {fullName}
                {whereClause}
                ORDER BY {(orderByClause ?? "(SELECT NULL)")}
                OFFSET @offset ROWS
                FETCH NEXT @pageSize ROWS ONLY
                """,
            DatabaseProvider.PostgreSql or DatabaseProvider.MySql => $"""
                SELECT *
                FROM {fullName}
                {whereClause}
                {(orderByClause is not null ? "ORDER BY " + orderByClause : "")}
                LIMIT @pageSize OFFSET @offset
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        args.Add("offset", offset);
        args.Add("pageSize", pageSize);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rawRows = await conn.QueryAsync(
            new CommandDefinition(dataSql,
                args,
                commandTimeout: _options.QueryTimeoutSeconds,
                cancellationToken: ct));

        var dataRows = rawRows
            .Select(r =>
            {
                var dict = (IDictionary<string, object>)r;
                // Dapper returns DBNull.Value for SQL NULLs — map to null.
                return new DataRow(dict.ToDictionary(k => k.Key, k => k.Value is DBNull ? null : (object?)k.Value));
            })
            .ToList();

        sw.Stop();
        logger.LogInformation(
            "Paged data fetched from {Schema}.{Object}: page {Page}/{PageSize}, total {Total}",
            schemaName, objectName, pageNumber, pageSize, totalCount);
        profiler.Record(factory.Provider.ToString(), $"SELECT * FROM {schemaName}.{objectName} (page {pageNumber})", sw.ElapsedMilliseconds, dataRows.Count);

        return new PagedResult<DataRow>(dataRows, pageNumber, pageSize, totalCount);
    }

    public async Task<IReadOnlyList<string>> GetPrimaryKeyColumnsAsync(
        string schemaName,
        string objectName,
        CancellationToken ct = default)
    {
        dialect.ThrowIfInvalidIdentifier(schemaName, nameof(schemaName));
        dialect.ThrowIfInvalidIdentifier(objectName, nameof(objectName));
        await validator.ValidateObjectAsync(schemaName, objectName, ct);

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);
        return await GetPrimaryKeyColumnsAsync(conn, schemaName, objectName, ct);
    }

    public async Task<ColumnStats> GetColumnStatsAsync(
        string schemaName,
        string objectName,
        string columnName,
        IReadOnlyList<ColumnFilter>? filters = null,
        CancellationToken ct = default)
    {
        dialect.ThrowIfInvalidIdentifier(schemaName, nameof(schemaName));
        dialect.ThrowIfInvalidIdentifier(objectName, nameof(objectName));
        dialect.ThrowIfInvalidIdentifier(columnName, nameof(columnName));
        await validator.ValidateObjectAsync(schemaName, objectName, ct);

        var fullName = dialect.QuoteQualifiedName(schemaName, objectName);
        var col = dialect.QuoteIdentifier(columnName);

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var args = new DynamicParameters();
        var whereClause = string.Empty;
        if (filters is { Count: > 0 })
        {
            var catalogColumns = await GetColumnNamesAsync(conn, schemaName, objectName, ct);
            var predicates = new List<string>();
            var i = 0;
            foreach (var filter in filters)
            {
                if (string.IsNullOrWhiteSpace(filter.ColumnName) || !catalogColumns.Contains(filter.ColumnName))
                    continue;

                dialect.ThrowIfInvalidIdentifier(filter.ColumnName, nameof(filter.ColumnName));
                var predicate = FilterSql.BuildPredicate(
                    dialect.QuoteIdentifier(filter.ColumnName), $"f{i++}", filter, args);
                if (predicate is not null)
                    predicates.Add(predicate);
            }

            if (predicates.Count > 0)
                whereClause = "WHERE " + string.Join(" AND ", predicates);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var countSql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
                $"SELECT COUNT_BIG(*) AS RowCount0, COUNT_BIG({col}) AS NonNullCount, COUNT_BIG(DISTINCT {col}) AS DistinctCount FROM {fullName} {whereClause}",
            DatabaseProvider.PostgreSql or DatabaseProvider.MySql =>
                $"SELECT COUNT(*) AS RowCount0, COUNT({col}) AS NonNullCount, COUNT(DISTINCT {col}) AS DistinctCount FROM {fullName} {whereClause}",
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var counts = await conn.QuerySingleAsync(
            new CommandDefinition(countSql, args, commandTimeout: _options.QueryTimeoutSeconds, cancellationToken: ct));

        string? minValue = null, maxValue = null;
        try
        {
            var minMax = await conn.QuerySingleAsync(
                new CommandDefinition(
                    $"SELECT MIN({col}) AS MinValue, MAX({col}) AS MaxValue FROM {fullName} {whereClause}",
                    args,
                    commandTimeout: _options.QueryTimeoutSeconds,
                    cancellationToken: ct));
            minValue = ((object?)minMax.MinValue) is DBNull or null ? null : Convert.ToString(minMax.MinValue);
            maxValue = ((object?)minMax.MaxValue) is DBNull or null ? null : Convert.ToString(minMax.MaxValue);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "MIN/MAX unavailable for {Schema}.{Object}.{Column}", schemaName, objectName, columnName);
        }

        sw.Stop();
        profiler.Record(factory.Provider.ToString(), $"Column stats {schemaName}.{objectName}.{columnName}", sw.ElapsedMilliseconds, 1);

        return new ColumnStats(
            columnName,
            (long)counts.RowCount0,
            (long)counts.NonNullCount,
            (long)counts.DistinctCount,
            minValue,
            maxValue);
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

    private async Task<HashSet<string>> GetColumnNamesAsync(
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
                FROM sys.columns c
                JOIN sys.objects o ON c.object_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE s.name = @schema
                  AND o.name = @obj
                """,
            DatabaseProvider.PostgreSql or DatabaseProvider.MySql =>
                """
                SELECT c.column_name
                FROM information_schema.columns c
                WHERE c.table_schema = @schema
                  AND c.table_name = @obj
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var rows = await conn.QueryAsync<string>(
            new CommandDefinition(
                sql,
                new { schema = schemaName, obj = objectName },
                commandTimeout: _options.QueryTimeoutSeconds,
                cancellationToken: ct));

        return rows.ToHashSet(StringComparer.Ordinal);
    }
}
