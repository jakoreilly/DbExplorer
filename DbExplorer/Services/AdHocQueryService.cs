using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DbExplorer.Services;

/// <summary>
/// Executes ad-hoc queries, EXPLAIN plans, and live activity queries
/// against the currently selected database connection.
/// Only SELECT statements (and CTEs that resolve to SELECT) are permitted.
/// All operations use the authenticated user's configured credentials.
/// </summary>
public sealed class AdHocQueryService(
    IDbConnectionFactory factory,
    ILogger<AdHocQueryService> logger) : IAdHocQueryService
{
    private const int DefaultTimeoutSeconds = 30;

    // Strip block comments before keyword analysis.
    private static readonly Regex _blockComment = new(
        @"/\*.*?\*/",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Strip single-line comments (-- to end of line) before DML keyword scan.
    private static readonly Regex _lineComment = new(
        @"--[^\n]*",
        RegexOptions.Compiled);

    // Matches the first meaningful keyword of a SQL statement after comment removal.
    // Permitted: SELECT, WITH (CTE), SHOW, EXPLAIN, DESCRIBE, DESC.
    private static readonly Regex _allowedLeadKeyword = new(
        @"^\s*(?:SELECT|WITH|SHOW|EXPLAIN|DESCRIBE|DESC)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Detects a second non-whitespace statement after a semicolon (multi-statement guard).
    private static readonly Regex _multiStatement = new(
        @";\s*\S",
        RegexOptions.Compiled);

    // Detects DML/DDL keywords that are dangerous inside CTEs (e.g. WITH x AS (INSERT ...)).
    // Only applied after both block and line comments have been stripped.
    private static readonly Regex _writeDml = new(
        @"\b(?:INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|CREATE|TRUNCATE|EXEC|EXECUTE|CALL|GRANT|REVOKE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Validates that <paramref name="sql"/> is a read-only SELECT/CTE/SHOW/EXPLAIN statement.
    /// Throws <see cref="InvalidOperationException"/> on any violation.
    /// This is defence-in-depth; a read-only database credential is the primary control.
    /// </summary>
    private static void EnsureReadOnly(string sql)
    {
        // Remove block and line comments so they cannot be used to mask or smuggle keywords.
        var stripped = _blockComment.Replace(sql, " ");
        stripped = _lineComment.Replace(stripped, " ");

        // Reject multi-statement batches (e.g. SELECT 1; DELETE FROM x).
        if (_multiStatement.IsMatch(stripped))
            throw new InvalidOperationException(
                "Only a single statement is permitted. Multi-statement batches are not allowed.");

        // Require the statement to begin with a permitted read-only keyword.
        if (!_allowedLeadKeyword.IsMatch(stripped))
            throw new InvalidOperationException(
                "Only SELECT, WITH (CTE), SHOW, EXPLAIN, DESCRIBE statements are permitted. " +
                "Data-modifying and DDL statements are not allowed.");

        // Guard against WITH … AS (INSERT/UPDATE/…) SELECT … writable CTEs.
        if (_writeDml.IsMatch(stripped))
            throw new InvalidOperationException(
                "Data-modifying keywords (INSERT, UPDATE, DELETE, DROP, etc.) are not permitted, " +
                "including inside CTEs.");
    }

    /// <summary>
    /// Executes a read-only SQL statement and returns columns + rows.
    /// Results are capped at <paramref name="maxRows"/> to prevent memory exhaustion.
    /// </summary>
    public async Task<QueryResult> ExecuteQueryAsync(
        string sql,
        int maxRows = 1000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return new QueryResult([], [], 0, null);

        EnsureReadOnly(sql);

        var sw = Stopwatch.StartNew();

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = DefaultTimeoutSeconds;

        var columns = new List<QueryResultColumn>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        for (var i = 0; i < reader.FieldCount; i++)
            columns.Add(new QueryResultColumn(reader.GetName(i)));

        int count = 0;
        while (await reader.ReadAsync(ct) && count < maxRows)
        {
            var row = new Dictionary<string, object?>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
                row[columns[i].Name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
            count++;
        }

        sw.Stop();
        logger.LogDebug("AdHoc [{Provider}] {RowCount} row(s) in {ElapsedMs}ms",
            factory.Provider, count, sw.ElapsedMilliseconds);

        var warning = count == maxRows ? $"Results capped at {maxRows} rows." : null;
        return new QueryResult(columns, rows, sw.ElapsedMilliseconds, warning);
    }

    /// <summary>
    /// Returns the estimated execution plan for a read-only SQL statement.
    /// </summary>
    public async Task<string> ExplainQueryAsync(string sql, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return string.Empty;

        EnsureReadOnly(sql);

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        return factory.Provider switch
        {
            DatabaseProvider.PostgreSql => await ExplainPostgreSqlAsync(conn, sql, ct),
            DatabaseProvider.MySql => await ExplainMySqlAsync(conn, sql, ct),
            DatabaseProvider.SqlServer => await ExplainSqlServerAsync(conn, sql, ct),
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };
    }

    /// <summary>
    /// Returns a snapshot of currently executing queries on the database server.
    /// </summary>
    public Task<QueryResult> GetActivityAsync(CancellationToken ct = default)
    {
        var sql = factory.Provider switch
        {
            DatabaseProvider.PostgreSql =>
                """
                SELECT pid::text       AS pid,
                       usename         AS username,
                       datname         AS database,
                       state           AS state,
                       wait_event_type AS wait_event_type,
                       wait_event      AS wait_event,
                       COALESCE(EXTRACT(EPOCH FROM (now() - query_start))::numeric(10,1)::text, '')
                                       AS duration_sec,
                       left(query, 300) AS query
                FROM   pg_stat_activity
                WHERE  state IS NOT NULL
                  AND  query IS NOT NULL
                  AND  query <> '<idle>'
                ORDER  BY query_start DESC NULLS LAST
                """,
            DatabaseProvider.SqlServer =>
                """
                SELECT CAST(r.session_id AS VARCHAR(10))              AS pid,
                       s.login_name                                   AS username,
                       DB_NAME(r.database_id)                        AS [database],
                       r.status                                       AS state,
                       ISNULL(r.wait_type, '')                        AS wait_type,
                       CAST(r.total_elapsed_time / 1000.0 AS DECIMAL(10,1)) AS duration_sec,
                       LEFT(t.text, 300)                              AS query
                FROM   sys.dm_exec_requests  r
                JOIN   sys.dm_exec_sessions  s ON r.session_id = s.session_id
                CROSS  APPLY sys.dm_exec_sql_text(r.sql_handle) t
                WHERE  r.session_id <> @@SPID
                ORDER  BY r.total_elapsed_time DESC
                """,
            DatabaseProvider.MySql =>
                """
                SELECT CAST(id AS CHAR)  AS pid,
                       user              AS username,
                       db               AS `database`,
                       state            AS state,
                       command          AS command,
                       CAST(time AS CHAR) AS duration_sec,
                       LEFT(info, 300)  AS query
                FROM   information_schema.PROCESSLIST
                WHERE  command <> 'Sleep'
                ORDER  BY time DESC
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        // Activity queries are internal system SELECTs — route through ExecuteQueryAsync.
        // EnsureReadOnly will pass them since they start with SELECT.
        return ExecuteQueryAsync(sql, maxRows: 200, ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<string> ExplainPostgreSqlAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"EXPLAIN (FORMAT TEXT) {sql}";
        cmd.CommandTimeout = DefaultTimeoutSeconds;

        var sb = new StringBuilder();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            sb.AppendLine(reader.IsDBNull(0) ? "" : reader.GetString(0));
        return sb.ToString();
    }

    private static async Task<string> ExplainMySqlAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"EXPLAIN {sql}";
        cmd.CommandTimeout = DefaultTimeoutSeconds;

        var sb = new StringBuilder();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var headers = Enumerable.Range(0, reader.FieldCount)
            .Select(i => reader.GetName(i))
            .ToList();
        sb.AppendLine(string.Join(" | ", headers.Select(h => h.PadRight(14))));
        sb.AppendLine(new string('-', headers.Count * 16));

        while (await reader.ReadAsync(ct))
        {
            var vals = Enumerable.Range(0, reader.FieldCount)
                .Select(i => (reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "")
                    .PadRight(14));
            sb.AppendLine(string.Join(" | ", vals));
        }
        return sb.ToString();
    }

    private static async Task<string> ExplainSqlServerAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using (var setOn = conn.CreateCommand())
        {
            setOn.CommandText = "SET SHOWPLAN_TEXT ON";
            setOn.CommandTimeout = DefaultTimeoutSeconds;
            await setOn.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var sb = new StringBuilder();
            await using var execCmd = conn.CreateCommand();
            execCmd.CommandText = sql;
            execCmd.CommandTimeout = DefaultTimeoutSeconds;

            await using var reader = await execCmd.ExecuteReaderAsync(ct);
            do
            {
                while (await reader.ReadAsync(ct))
                    sb.AppendLine(reader.IsDBNull(0) ? "" : reader.GetValue(0)?.ToString());
            }
            while (await reader.NextResultAsync(ct));

            return sb.ToString();
        }
        finally
        {
            // Always reset even if the query or cancellation throws, so that a pooled
            // connection is not returned with SHOWPLAN_TEXT still enabled.
            await using var setOff = conn.CreateCommand();
            setOff.CommandText = "SET SHOWPLAN_TEXT OFF";
            setOff.CommandTimeout = DefaultTimeoutSeconds;
            await setOff.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
