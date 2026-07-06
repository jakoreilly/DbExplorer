using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Options;
using Microsoft.Extensions.Options;
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
    IOptions<ProfilerOptions> profilerOptions,
    ILogger<AdHocQueryService> logger,
    ISystemAnalyserStore analyser) : IAdHocQueryService
{
    private int TimeoutSeconds => profilerOptions.Value.QueryTimeoutSeconds;

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

    // Strips single-quoted string literals so their contents can't mask or smuggle keywords.
    // Handles doubled-quote escapes (e.g. 'it''s') correctly.
    private static readonly Regex _stringLiteral = new(
        @"'(?:[^']|'')*'",
        RegexOptions.Compiled);

    // Detects DML/DDL keywords that are dangerous inside CTEs (e.g. WITH x AS (INSERT ...)).
    // Only applied after both block and line comments AND string literals have been stripped.
    // Includes provider-specific bulk-load commands (LOAD DATA for MySQL, COPY for PostgreSQL),
    // and SQL Server-specific commands that allow file system access or dynamic SQL execution.
    private static readonly Regex _writeDml = new(
        @"\b(?:INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|CREATE|TRUNCATE|EXEC|EXECUTE|CALL|GRANT|REVOKE|LOAD|COPY"
        + @"|BULK|OPENROWSET|OPENDATASOURCE|XP_CMDSHELL|SP_EXECUTESQL|SP_EXECUTE)\b",
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
        // Strip string literal contents so semicolons/DML inside strings don't cause
        // false positives (e.g. SELECT ';DELETE' or SELECT 'LOAD DATA...').
        stripped = _stringLiteral.Replace(stripped, "''");

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
        cmd.CommandTimeout = TimeoutSeconds;

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
            DatabaseProvider.PostgreSql => await ExplainPostgreSqlAsync(conn, sql, TimeoutSeconds, ct),
            DatabaseProvider.MySql => await ExplainMySqlAsync(conn, sql, TimeoutSeconds, ct),
            DatabaseProvider.SqlServer => await ExplainSqlServerAsync(conn, sql, TimeoutSeconds, ct),
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };
    }

    /// <summary>
    /// Returns a snapshot of all current sessions (including idle/sleeping) on the server.
    /// </summary>
    public Task<QueryResult> GetActivityAsync(CancellationToken ct = default)
    {
        var sql = factory.Provider switch
        {
            DatabaseProvider.PostgreSql =>
                """
                SELECT pid::text                AS pid,
                       usename                  AS username,
                       datname                  AS database,
                       state                    AS state,
                       wait_event_type          AS wait_event_type,
                       wait_event               AS wait_event,
                       COALESCE(EXTRACT(EPOCH FROM (now() - query_start))::numeric(10,1)::text, '')
                                                AS duration_sec,
                       backend_type             AS backend_type,
                       left(query, 500)         AS query
                FROM   pg_stat_activity
                WHERE  pid != pg_backend_pid()
                  AND  backend_type = 'client backend'
                ORDER  BY state, query_start DESC NULLS LAST
                """,
            DatabaseProvider.SqlServer =>
                """
                SELECT CAST(s.session_id AS VARCHAR(10))                          AS pid,
                       s.login_name                                               AS username,
                       DB_NAME(s.database_id)                                    AS [database],
                       ISNULL(r.status, s.status)                                AS state,
                       ISNULL(r.wait_type, '')                                   AS wait_type,
                       CAST(ISNULL(r.total_elapsed_time, 0) / 1000.0 AS DECIMAL(10,1)) AS duration_sec,
                       ISNULL(LEFT(t.text, 500), '')                             AS query
                FROM   sys.dm_exec_sessions  s
                LEFT   JOIN sys.dm_exec_requests  r ON s.session_id = r.session_id
                OUTER  APPLY sys.dm_exec_sql_text(r.sql_handle)                  t
                WHERE  s.is_user_process = 1
                  AND  s.session_id <> @@SPID
                ORDER  BY duration_sec DESC
                """,
            DatabaseProvider.MySql =>
                """
                SELECT CAST(id AS CHAR)    AS pid,
                       user               AS username,
                       db                AS `database`,
                       state             AS state,
                       command           AS command,
                       CAST(time AS CHAR) AS duration_sec,
                       LEFT(info, 500)   AS query
                FROM   information_schema.PROCESSLIST
                WHERE  id <> CONNECTION_ID()
                ORDER  BY time DESC
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        return ExecuteQueryAsync(sql, maxRows: 200, ct);
    }

    /// <summary>
    /// Returns recently executed queries from provider-specific statistics views.
    /// Returns a <see cref="QueryResult"/> with <see cref="QueryResult.Warning"/> set when
    /// the required feature (pg_stat_statements, performance_schema) is unavailable.
    /// </summary>
    public async Task<QueryResult> GetRecentQueriesAsync(CancellationToken ct = default)
    {
        var sql = factory.Provider switch
        {
            DatabaseProvider.PostgreSql =>
                """
                SELECT query,
                       calls                                          AS executions,
                       CAST(total_exec_time / calls AS DECIMAL(10,2)) AS avg_ms,
                       CAST(max_exec_time           AS DECIMAL(10,2)) AS max_ms,
                       rows
                FROM   pg_stat_statements
                WHERE  query NOT LIKE '%pg_stat_statements%'
                ORDER  BY last_exec DESC NULLS LAST
                LIMIT  100
                """,
            DatabaseProvider.SqlServer =>
                """
                SELECT TOP 100
                       CAST(qs.execution_count AS VARCHAR)                                    AS executions,
                       CAST(qs.total_elapsed_time / qs.execution_count / 1000.0 AS DECIMAL(10,1)) AS avg_ms,
                       CAST(qs.max_elapsed_time / 1000.0                         AS DECIMAL(10,1)) AS max_ms,
                       CONVERT(VARCHAR(23), qs.last_execution_time, 121)                     AS last_executed,
                       LEFT(t.text, 500)                                                      AS query
                FROM   sys.dm_exec_query_stats   qs
                CROSS  APPLY sys.dm_exec_sql_text(qs.sql_handle) t
                ORDER  BY qs.last_execution_time DESC
                """,
            DatabaseProvider.MySql =>
                """
                SELECT LEFT(DIGEST_TEXT, 500)                                              AS query,
                       COUNT_STAR                                                           AS executions,
                       CAST(AVG_TIMER_WAIT / 1000000000.0 AS DECIMAL(10,3))               AS avg_ms,
                       CAST(MAX_TIMER_WAIT / 1000000000.0 AS DECIMAL(10,3))               AS max_ms,
                       DATE_FORMAT(LAST_SEEN, '%Y-%m-%d %H:%i:%s')                        AS last_executed
                FROM   performance_schema.events_statements_summary_by_digest
                WHERE  DIGEST_TEXT IS NOT NULL
                ORDER  BY LAST_SEEN DESC
                LIMIT  100
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        try
        {
            return await ExecuteQueryAsync(sql, maxRows: 100, ct);
        }
        catch (System.Data.Common.DbException ex)
        {
            // Surface a helpful message when the required feature is not enabled.
            var hint = factory.Provider switch
            {
                DatabaseProvider.PostgreSql =>
                    "Requires the pg_stat_statements extension. Enable it with: CREATE EXTENSION pg_stat_statements;",
                DatabaseProvider.MySql =>
                    "Requires performance_schema to be enabled on the server.",
                _ => ex.Message
            };
            logger.LogWarning(ex, "GetRecentQueriesAsync unavailable for {Provider}", factory.Provider);
            analyser.RecordError(factory.Provider.ToString(), DbActionCategory.AdHocQuery, "GetRecentQueries", ex);
            return new QueryResult([], [], 0, hint);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<string> ExplainPostgreSqlAsync(DbConnection conn, string sql, int timeout, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"EXPLAIN (FORMAT TEXT) {sql}";
        cmd.CommandTimeout = timeout;

        var sb = new StringBuilder();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            sb.AppendLine(reader.IsDBNull(0) ? "" : reader.GetString(0));
        return sb.ToString();
    }

    private static async Task<string> ExplainMySqlAsync(DbConnection conn, string sql, int timeout, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"EXPLAIN {sql}";
        cmd.CommandTimeout = timeout;

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

    private static async Task<string> ExplainSqlServerAsync(DbConnection conn, string sql, int timeout, CancellationToken ct)
    {
        await using (var setOn = conn.CreateCommand())
        {
            setOn.CommandText = "SET SHOWPLAN_TEXT ON";
            setOn.CommandTimeout = timeout;
            await setOn.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var sb = new StringBuilder();
            await using var execCmd = conn.CreateCommand();
            execCmd.CommandText = sql;
            execCmd.CommandTimeout = timeout;

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
            setOff.CommandTimeout = timeout;
            await setOff.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
