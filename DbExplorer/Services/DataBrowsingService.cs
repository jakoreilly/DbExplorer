using Dapper;
using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Core.Validation;
using Microsoft.Data.SqlClient;

namespace DbExplorer.Services;

/// <summary>
/// Queries paged data from tables and views.
/// Identifiers are bracket-quoted after catalog validation; user values never enter SQL text.
/// </summary>
public sealed class DataBrowsingService(
    SqlConnectionFactory factory,
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
        SqlIdentifierHelper.ThrowIfInvalidFormat(schemaName, nameof(schemaName));
        SqlIdentifierHelper.ThrowIfInvalidFormat(objectName, nameof(objectName));

        // 2. Validate against live catalog
        await validator.ValidateObjectAsync(schemaName, objectName, ct);

        // 3. Clamp page size
        var pageSize = Math.Clamp(paging.PageSize, 1, MaxPageSize);
        var pageNumber = Math.Max(1, paging.PageNumber);
        var offset = (pageNumber - 1) * pageSize;

        // 4. Bracket-quote validated identifiers
        var quotedSchema = SqlIdentifierHelper.Quote(schemaName);
        var quotedObject = SqlIdentifierHelper.Quote(objectName);
        var fullName = $"{quotedSchema}.{quotedObject}";

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        // Count
        var totalCount = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                $"SELECT COUNT_BIG(*) FROM {fullName}",
                commandTimeout: QueryTimeoutSeconds,
                cancellationToken: ct));

        // Data — OFFSET/FETCH requires ORDER BY; use (SELECT NULL) for unordered
        var dataSql = $"""
            SELECT *
            FROM {fullName}
            ORDER BY (SELECT NULL)
            OFFSET @offset ROWS
            FETCH NEXT @pageSize ROWS ONLY
            """;

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
