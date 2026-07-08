using System.Collections.Concurrent;
using Dapper;

namespace DbExplorer.Services;

public sealed record DataValueSearchRequest(
    string Value,
    IReadOnlyList<string> ConnectionNames,
    string? TablePattern,
    string? ColumnPattern);

public sealed record DataColumnSample(string ColumnName, string SampleValue);

public sealed record DataTableMatch(
    string CatalogName,
    string SchemaName,
    string TableName,
    IReadOnlyList<DataColumnSample> Samples,
    int RowsSampled);

public sealed record DataConnectionResult(
    string ConnectionName,
    DatabaseProvider Provider,
    IReadOnlyList<DataTableMatch> Matches,
    int TablesScanned,
    int CandidateTables,
    bool Truncated,
    string? Error);

/// <summary>
/// Phase 2 of cross-connection search: probes actual row values for a literal string across the
/// text columns of base tables in every accessible database on each selected connection.
///
/// Deliberately conservative — full-text scans are expensive. Candidate tables are capped per
/// database, only a handful of sample rows are returned per table, each probe has its own short
/// timeout, and probes run with bounded concurrency so no single server is overwhelmed. Users
/// narrow the surface with table/column name patterns.
/// </summary>
public sealed class DataValueSearchService
{
    /// <summary>Above this many candidate tables (across all scanned databases), the connection is not scanned.</summary>
    public const int MaxCandidateTables = 300;
    /// <summary>Databases scanned per connection.</summary>
    public const int MaxCatalogs = 25;
    /// <summary>Sample rows returned per matching table.</summary>
    public const int SampleRows = 5;
    /// <summary>Per-table probe timeout (seconds); a slow full scan is abandoned, not fatal.</summary>
    public const int PerTableTimeoutSeconds = 10;
    private const int ProbeConcurrency = 4;
    private const int SampleValueMaxLength = 200;

    private readonly DatabaseSelectorState _selectorState;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DataValueSearchService> _logger;

    public DataValueSearchService(
        DatabaseSelectorState selectorState,
        IConfiguration configuration,
        ILogger<DataValueSearchService> logger)
    {
        _selectorState = selectorState;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DataConnectionResult>> SearchAsync(
        DataValueSearchRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Value))
            return [];

        var selected = _selectorState.Options
            .Where(o => request.ConnectionNames.Contains(o.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var tasks = selected.Select(o => SearchConnectionAsync(o, request, ct)).ToList();
        return await Task.WhenAll(tasks);
    }

    private async Task<DataConnectionResult> SearchConnectionAsync(
        DatabaseConnectionOption option, DataValueSearchRequest request, CancellationToken ct)
    {
        try
        {
            var baseConnectionString = _configuration.GetConnectionString(option.ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{option.ConnectionStringName}' is not configured.");

            var catalogs = (await ConnectionCatalogHelper.GetCatalogsAsync(option.Provider, baseConnectionString, 15, ct))
                .Take(MaxCatalogs).ToList();

            // Gather candidate (catalog, schema, table, columns) across all databases first, so the
            // total-table cap is applied to the whole connection rather than per-database.
            var candidates = new List<CandidateTable>();
            foreach (var catalog in catalogs)
            {
                var cols = await GetCandidateColumnsAsync(option, baseConnectionString, catalog, request, ct);
                candidates.AddRange(cols
                    .GroupBy(c => (c.SchemaName, c.TableName))
                    .Select(g => new CandidateTable(catalog, g.Key.SchemaName, g.Key.TableName,
                        g.Select(c => c.ColumnName).ToList())));
            }

            if (candidates.Count > MaxCandidateTables)
            {
                return new DataConnectionResult(option.Name, option.Provider, [], 0, candidates.Count,
                    Truncated: true,
                    Error: $"{candidates.Count} candidate tables match your filters across {catalogs.Count} database(s) — " +
                           "too many to scan safely. Add or tighten the table/column name filters.");
            }

            var likePattern = "%" + EscapeLike(request.Value.Trim()) + "%";
            var matches = new ConcurrentBag<DataTableMatch>();
            var scanned = 0;
            using var gate = new SemaphoreSlim(ProbeConcurrency);

            var probes = candidates.Select(async t =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var pinned = ConnectionCatalogHelper.WithCatalog(option.Provider, baseConnectionString, t.CatalogName);
                    var factory = new DbConnectionFactory(option.Provider, pinned);
                    var dialect = new SqlDialect(option.Provider);
                    var match = await ProbeTableAsync(factory, dialect, option.Provider,
                        t.CatalogName, t.SchemaName, t.TableName, t.Columns, request.Value.Trim(), likePattern, ct);
                    Interlocked.Increment(ref scanned);
                    if (match is not null) matches.Add(match);
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(probes);

            var ordered = matches
                .OrderBy(m => m.CatalogName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.SchemaName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.TableName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new DataConnectionResult(option.Name, option.Provider, ordered,
                scanned, candidates.Count, Truncated: false, Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Data-value search failed for connection {Connection}", option.Name);
            return new DataConnectionResult(option.Name, option.Provider, [], 0, 0, false,
                "This connection could not be searched — it may be unreachable or misconfigured.");
        }
    }

    private static async Task<IReadOnlyList<CandidateColumn>> GetCandidateColumnsAsync(
        DatabaseConnectionOption option, string baseConnectionString, string catalog,
        DataValueSearchRequest request, CancellationToken ct)
    {
        var tableFilter = string.IsNullOrWhiteSpace(request.TablePattern)
            ? null : "%" + EscapeLike(request.TablePattern.Trim()) + "%";
        var columnFilter = string.IsNullOrWhiteSpace(request.ColumnPattern)
            ? null : "%" + EscapeLike(request.ColumnPattern.Trim()) + "%";

        var sql = option.Provider switch
        {
            DatabaseProvider.SqlServer => """
                SELECT c.TABLE_SCHEMA AS SchemaName, c.TABLE_NAME AS TableName, c.COLUMN_NAME AS ColumnName
                FROM INFORMATION_SCHEMA.COLUMNS c
                JOIN INFORMATION_SCHEMA.TABLES t
                  ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
                WHERE t.TABLE_TYPE = 'BASE TABLE'
                  AND c.DATA_TYPE IN ('char','varchar','nchar','nvarchar','text','ntext','sysname','uniqueidentifier')
                  AND (@tableFilter IS NULL OR c.TABLE_NAME LIKE @tableFilter ESCAPE '\')
                  AND (@columnFilter IS NULL OR c.COLUMN_NAME LIKE @columnFilter ESCAPE '\')
                ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
                """,
            DatabaseProvider.PostgreSql => """
                SELECT c.table_schema AS "SchemaName", c.table_name AS "TableName", c.column_name AS "ColumnName"
                FROM information_schema.columns c
                JOIN information_schema.tables t
                  ON t.table_schema = c.table_schema AND t.table_name = c.table_name
                WHERE t.table_type = 'BASE TABLE'
                  AND c.table_schema NOT IN ('pg_catalog', 'information_schema')
                  AND c.data_type IN ('character varying','character','text','citext','uuid','name')
                  AND (@tableFilter IS NULL OR c.table_name ILIKE @tableFilter ESCAPE '\')
                  AND (@columnFilter IS NULL OR c.column_name ILIKE @columnFilter ESCAPE '\')
                ORDER BY c.table_schema, c.table_name, c.ordinal_position
                """,
            DatabaseProvider.MySql => """
                SELECT c.table_schema AS SchemaName, c.table_name AS TableName, c.column_name AS ColumnName
                FROM information_schema.columns c
                JOIN information_schema.tables t
                  ON t.table_schema = c.table_schema AND t.table_name = c.table_name
                WHERE t.table_type = 'BASE TABLE'
                  AND c.table_schema = DATABASE()
                  AND c.data_type IN ('char','varchar','text','tinytext','mediumtext','longtext','enum','set')
                  AND (@tableFilter IS NULL OR c.table_name LIKE @tableFilter ESCAPE '\\')
                  AND (@columnFilter IS NULL OR c.column_name LIKE @columnFilter ESCAPE '\\')
                ORDER BY c.table_schema, c.table_name, c.ordinal_position
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{option.Provider}'.")
        };

        var pinned = ConnectionCatalogHelper.WithCatalog(option.Provider, baseConnectionString, catalog);
        var factory = new DbConnectionFactory(option.Provider, pinned);
        await using var connection = factory.Create();
        var rows = await connection.QueryAsync<CandidateColumn>(
            new CommandDefinition(sql, new { tableFilter, columnFilter },
                commandTimeout: 30, cancellationToken: ct));
        return rows.ToList();
    }

    private async Task<DataTableMatch?> ProbeTableAsync(
        IDbConnectionFactory factory, SqlDialect dialect, DatabaseProvider provider,
        string catalog, string schema, string table, IReadOnlyList<string> columns,
        string term, string likePattern, CancellationToken ct)
    {
        try
        {
            var qualified = dialect.QuoteQualifiedName(schema, table);
            var selectList = string.Join(", ", columns.Select(dialect.QuoteIdentifier));
            var likeOp = provider == DatabaseProvider.PostgreSql ? "ILIKE" : "LIKE";
            var escape = provider == DatabaseProvider.MySql ? "'\\\\'" : "'\\'";
            var whereOr = string.Join(" OR ",
                columns.Select(c => $"{dialect.QuoteIdentifier(c)} {likeOp} @v ESCAPE {escape}"));

            var sql = provider == DatabaseProvider.SqlServer
                ? $"SELECT TOP {SampleRows} {selectList} FROM {qualified} WHERE {whereOr}"
                : $"SELECT {selectList} FROM {qualified} WHERE {whereOr} LIMIT {SampleRows}";

            await using var connection = factory.Create();
            var rows = (await connection.QueryAsync(
                new CommandDefinition(sql, new { v = likePattern },
                    commandTimeout: PerTableTimeoutSeconds, cancellationToken: ct)))
                .Cast<IDictionary<string, object>>()
                .ToList();

            if (rows.Count == 0) return null;

            var samples = new List<DataColumnSample>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                foreach (var (key, value) in row)
                {
                    if (seen.Contains(key)) continue;
                    var text = value?.ToString();
                    if (text is null || text.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    seen.Add(key);
                    samples.Add(new DataColumnSample(key, Truncate(text)));
                }
            }

            return new DataTableMatch(catalog, schema, table, samples, rows.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Probe skipped for {Catalog}.{Schema}.{Table}", catalog, schema, table);
            return null;
        }
    }

    /// <summary>
    /// Builds a read-only SELECT that finds the matching rows, ready to paste into the Profiler.
    /// The term is embedded as an escaped literal so the user can run it as-is.
    /// </summary>
    public static string BuildLookupSql(
        DatabaseProvider provider, string schema, string table, string column, string term)
    {
        var dialect = new SqlDialect(provider);
        var qualified = dialect.QuoteQualifiedName(schema, table);
        var col = dialect.QuoteIdentifier(column);
        var literal = "'%" + term.Replace("'", "''") + "%'";
        var likeOp = provider == DatabaseProvider.PostgreSql ? "ILIKE" : "LIKE";
        return provider == DatabaseProvider.SqlServer
            ? $"SELECT TOP 100 * FROM {qualified} WHERE {col} {likeOp} {literal};"
            : $"SELECT * FROM {qualified} WHERE {col} {likeOp} {literal} LIMIT 100;";
    }

    private static string Truncate(string value) =>
        value.Length <= SampleValueMaxLength ? value : value[..SampleValueMaxLength] + "…";

    /// <summary>Escapes LIKE wildcards in the user's term so they match literally.</summary>
    internal static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_")
            .Replace("[", "\\[");

    private sealed record CandidateColumn(string SchemaName, string TableName, string ColumnName);
    private sealed record CandidateTable(string CatalogName, string SchemaName, string TableName, IReadOnlyList<string> Columns);
}
