using Dapper;
using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;

namespace DbExplorer.Services;

/// <summary>
/// Queries paged data from tables and views.
/// Identifiers are dialect-quoted after catalog validation; user values never enter SQL text.
/// </summary>
public sealed class DataBrowsingService(
    IDbConnectionFactory factory,
    SqlDialect dialect,
    IIdentifierValidator validator,
    ILogger<DataBrowsingService> logger) : IDataBrowsingService
{
    private const int MaxPageSize = 500;
    private const int DefaultPageSize = 50;
    private const int QueryTimeoutSeconds = 30;

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

        // 3. Clamp page size
        var pageSize = Math.Clamp(paging.PageSize, 1, MaxPageSize);
        var pageNumber = Math.Max(1, paging.PageNumber);
        var offset = (pageNumber - 1) * pageSize;

        // 4. Quote validated identifiers for active dialect
        var fullName = dialect.QuoteQualifiedName(schemaName, objectName);

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

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
                commandTimeout: QueryTimeoutSeconds,
                cancellationToken: ct));

        var dataSql = factory.Provider switch
        {
            DatabaseProvider.SqlServer => $"""
                SELECT *
                FROM {fullName}
                ORDER BY (SELECT NULL)
                OFFSET @offset ROWS
                FETCH NEXT @pageSize ROWS ONLY
                """,
            DatabaseProvider.PostgreSql or DatabaseProvider.MySql => $"""
                SELECT *
                FROM {fullName}
                LIMIT @pageSize OFFSET @offset
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var rawRows = await conn.QueryAsync(
            new CommandDefinition(dataSql,
                new { offset, pageSize },
                commandTimeout: QueryTimeoutSeconds,
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
}
