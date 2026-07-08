using System.Collections.Concurrent;
using Dapper;

namespace DbExplorer.Services;

public sealed record CrossDbColumnHit(
    string ConnectionName,
    DatabaseProvider Provider,
    string CatalogName,
    string SchemaName,
    string TableName,
    string ColumnName,
    string DataType);

public sealed record ConnectionSearchResult(
    string ConnectionName,
    DatabaseProvider Provider,
    IReadOnlyList<CrossDbColumnHit> Hits,
    string? Error);

/// <summary>
/// Searches table and column names across every configured connection and every accessible
/// database (catalog) on each — not just the connection string's default catalog. Connections
/// are queried in parallel; unreachable ones report an error instead of failing the whole search.
/// </summary>
public sealed class MetadataSearchService
{
    /// <summary>Per-connection cap so one match-everything term can't flood the page.</summary>
    public const int MaxHitsPerConnection = 500;
    /// <summary>Databases scanned per connection before results are capped.</summary>
    public const int MaxCatalogs = 60;
    private const int CatalogConcurrency = 4;

    private readonly DatabaseSelectorState _selectorState;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MetadataSearchService> _logger;

    public MetadataSearchService(
        DatabaseSelectorState selectorState,
        IConfiguration configuration,
        ILogger<MetadataSearchService> logger)
    {
        _selectorState = selectorState;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ConnectionSearchResult>> SearchAsync(string term, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term))
            return [];

        var pattern = "%" + EscapeLike(term.Trim()) + "%";
        var tasks = _selectorState.Options
            .Select(option => SearchConnectionAsync(option, pattern, ct))
            .ToList();
        return await Task.WhenAll(tasks);
    }

    private async Task<ConnectionSearchResult> SearchConnectionAsync(
        DatabaseConnectionOption option, string pattern, CancellationToken ct)
    {
        try
        {
            var baseConnectionString = _configuration.GetConnectionString(option.ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{option.ConnectionStringName}' is not configured.");

            var catalogs = (await ConnectionCatalogHelper.GetCatalogsAsync(option.Provider, baseConnectionString, 15, ct))
                .Take(MaxCatalogs).ToList();

            var hits = new ConcurrentBag<CrossDbColumnHit>();
            using var gate = new SemaphoreSlim(CatalogConcurrency);
            var scans = catalogs.Select(async catalog =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    foreach (var hit in await SearchCatalogAsync(option, baseConnectionString, catalog, pattern, ct))
                        hits.Add(hit);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Name search skipped catalog {Catalog} on {Connection}", catalog, option.Name);
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(scans);

            var ordered = hits
                .OrderBy(h => h.CatalogName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.SchemaName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.TableName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxHitsPerConnection)
                .ToList();
            return new ConnectionSearchResult(option.Name, option.Provider, ordered, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Metadata search failed for connection {Connection}", option.Name);
            return new ConnectionSearchResult(option.Name, option.Provider, [],
                "This connection could not be searched — it may be unreachable or misconfigured.");
        }
    }

    private static async Task<IReadOnlyList<CrossDbColumnHit>> SearchCatalogAsync(
        DatabaseConnectionOption option, string baseConnectionString, string catalog,
        string pattern, CancellationToken ct)
    {
        var pinned = ConnectionCatalogHelper.WithCatalog(option.Provider, baseConnectionString, catalog);
        var factory = new DbConnectionFactory(option.Provider, pinned);

        var sql = option.Provider switch
        {
            DatabaseProvider.SqlServer => $"""
                SELECT TOP {MaxHitsPerConnection}
                    TABLE_SCHEMA AS SchemaName, TABLE_NAME AS TableName,
                    COLUMN_NAME AS ColumnName, DATA_TYPE AS DataType
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME LIKE @pattern ESCAPE '\' OR COLUMN_NAME LIKE @pattern ESCAPE '\'
                ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION
                """,
            DatabaseProvider.PostgreSql => $"""
                SELECT table_schema AS "SchemaName", table_name AS "TableName",
                       column_name AS "ColumnName", data_type AS "DataType"
                FROM information_schema.columns
                WHERE (table_name ILIKE @pattern ESCAPE '\' OR column_name ILIKE @pattern ESCAPE '\')
                  AND table_schema NOT IN ('pg_catalog', 'information_schema')
                ORDER BY table_schema, table_name, ordinal_position
                LIMIT {MaxHitsPerConnection}
                """,
            DatabaseProvider.MySql => $"""
                SELECT table_schema AS SchemaName, table_name AS TableName,
                       column_name AS ColumnName, data_type AS DataType
                FROM information_schema.columns
                WHERE table_schema = DATABASE()
                  AND (table_name LIKE @pattern ESCAPE '\\' OR column_name LIKE @pattern ESCAPE '\\')
                ORDER BY table_name, ordinal_position
                LIMIT {MaxHitsPerConnection}
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{option.Provider}'.")
        };

        await using var connection = factory.Create();
        var rows = await connection.QueryAsync<ColumnRow>(
            new CommandDefinition(sql, new { pattern }, commandTimeout: 15, cancellationToken: ct));

        return rows
            .Select(r => new CrossDbColumnHit(option.Name, option.Provider, catalog,
                r.SchemaName, r.TableName, r.ColumnName, r.DataType))
            .ToList();
    }

    /// <summary>Escapes LIKE wildcards in the user's term so they match literally.</summary>
    internal static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_")
            .Replace("[", "\\[");

    private sealed record ColumnRow(string SchemaName, string TableName, string ColumnName, string DataType);
}
