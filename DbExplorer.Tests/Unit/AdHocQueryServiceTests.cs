using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Options;
using DbExplorer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace DbExplorer.Tests.Unit;

/// <summary>
/// Tests for the read-only guard inside AdHocQueryService.
/// We verify via ExecuteQueryAsync / ExplainQueryAsync since EnsureReadOnly is private.
/// </summary>
public class AdHocQueryServiceTests
{
    private static AdHocQueryService CreateService(DatabaseProvider provider = DatabaseProvider.SqlServer)
    {
        var factory = Mock.Of<IDbConnectionFactory>(f => f.Provider == provider);
        var options = Microsoft.Extensions.Options.Options.Create(new ProfilerOptions());
        return new AdHocQueryService(factory, options, NullLogger<AdHocQueryService>.Instance);
    }

    // ── Allowed statements ────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("select * from users")]
    [InlineData("  SELECT id FROM orders WHERE id = 1")]
    [InlineData("WITH cte AS (SELECT id FROM users) SELECT * FROM cte")]
    [InlineData("EXPLAIN SELECT * FROM users")]
    [InlineData("SHOW TABLES")]
    [InlineData("DESCRIBE users")]
    [InlineData("DESC users")]
    [InlineData("-- comment\nSELECT 1")]
    [InlineData("SELECT 1;")]                                        // trailing semicolon, no second statement
    [InlineData("SELECT id -- DELETE this field later\nFROM users")] // DML keyword only in line comment
    [InlineData("SELECT id /* old_col */ FROM users")]               // DML-free block comment
    public async Task ExecuteQueryAsync_AllowedStatements_DoesNotThrowValidationError(string sql)
    {
        // The query will fail to connect (factory is mocked), but EnsureReadOnly must pass first.
        // We assert that no InvalidOperationException was thrown — any connection-level
        // exception is acceptable here.
        var svc = CreateService();
        var ex = await Record.ExceptionAsync(() => svc.ExecuteQueryAsync(sql));
        Assert.False(ex is InvalidOperationException,
            $"Read-only guard incorrectly rejected: {sql}. Error: {ex?.Message}");
    }

    // ── Blocked: DML / DDL ───────────────────────────────────────────────────

    [Theory]
    [InlineData("DELETE FROM users")]
    [InlineData("delete from users")]
    [InlineData("UPDATE users SET name = 'x'")]
    [InlineData("INSERT INTO users VALUES (1)")]
    [InlineData("DROP TABLE users")]
    [InlineData("ALTER TABLE users ADD COLUMN x INT")]
    [InlineData("TRUNCATE TABLE users")]
    [InlineData("EXEC sp_helpdb")]
    [InlineData("EXECUTE sp_helpdb")]
    [InlineData("CALL my_proc()")]
    public async Task ExecuteQueryAsync_WriteStatements_ThrowInvalidOperationException(string sql)
    {
        var svc = CreateService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ExecuteQueryAsync(sql));
    }

    // ── Blocked: block-comment bypass ─────────────────────────────────────────
    // After stripping block comments, these reduce to DML/DDL with no SELECT prefix
    // so EnsureReadOnly fires on the lead-keyword check ("not allowed").

    [Theory]
    [InlineData("/* comment */ DELETE FROM users")]
    [InlineData("/* SELECT */ DELETE FROM users")]
    [InlineData("/**/ INSERT INTO users VALUES (1)")]
    [InlineData("/* with cte AS (SELECT 1) */ DROP TABLE x")]
    public async Task ExecuteQueryAsync_BlockCommentBypass_ThrowsInvalidOperationException(string sql)
    {
        var svc = CreateService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ExecuteQueryAsync(sql));
    }

    // ── Blocked: multi-statement batch ────────────────────────────────────────

    [Theory]
    [InlineData("SELECT 1; DELETE FROM users")]
    [InlineData("SELECT 1;DELETE FROM users")]
    [InlineData("SELECT 1; DROP TABLE x")]
    [InlineData("SELECT id FROM users; INSERT INTO log VALUES (1)")]
    public async Task ExecuteQueryAsync_MultiStatement_ThrowsInvalidOperationException(string sql)
    {
        var svc = CreateService();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ExecuteQueryAsync(sql));
        Assert.Contains("single statement", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Blocked: writable CTEs ────────────────────────────────────────────────

    [Theory]
    [InlineData("WITH x AS (INSERT INTO users VALUES (1)) SELECT * FROM x")]
    [InlineData("WITH d AS (DELETE FROM users RETURNING *) SELECT * FROM d")]
    [InlineData("WITH upd AS (UPDATE users SET name='x' RETURNING id) SELECT * FROM upd")]
    public async Task ExecuteQueryAsync_WritableCte_ThrowsInvalidOperationException(string sql)
    {
        var svc = CreateService();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ExecuteQueryAsync(sql));
        Assert.Contains("not permitted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── ExplainQueryAsync applies same guard ──────────────────────────────────

    [Fact]
    public async Task ExplainQueryAsync_DeleteStatement_ThrowsInvalidOperationException()
    {
        var svc = CreateService();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ExplainQueryAsync("DELETE FROM users"));
    }

    [Fact]
    public async Task ExplainQueryAsync_EmptyString_ReturnsEmpty()
    {
        var svc = CreateService();
        var result = await svc.ExplainQueryAsync("   ");
        Assert.Equal(string.Empty, result);
    }

    // ── ExecuteQueryAsync edge cases ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteQueryAsync_EmptyString_ReturnsEmptyResult()
    {
        var svc = CreateService();
        var result = await svc.ExecuteQueryAsync("  ");
        Assert.Empty(result.Columns);
        Assert.Empty(result.Rows);
    }

    // ── GetRecentQueriesAsync ─────────────────────────────────────────────────

    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.MySql)]
    [InlineData(DatabaseProvider.PostgreSql)]
    public async Task GetRecentQueriesAsync_AllProviders_DoNotThrowValidationError(DatabaseProvider provider)
    {
        // The query will fail to connect (factory is mocked), but EnsureReadOnly must pass
        // for the SELECT-based statistics queries.  A non-InvalidOperationException is
        // acceptable here (connection failure or DbException caught internally).
        var svc = CreateService(provider);
        var ex = await Record.ExceptionAsync(() => svc.GetRecentQueriesAsync());
        Assert.False(ex is InvalidOperationException,
            $"Read-only guard incorrectly rejected GetRecentQueriesAsync for {provider}: {ex?.Message}");
    }

    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.MySql)]
    [InlineData(DatabaseProvider.PostgreSql)]
    public async Task GetActivityAsync_AllProviders_DoNotThrowValidationError(DatabaseProvider provider)
    {
        var svc = CreateService(provider);
        var ex = await Record.ExceptionAsync(() => svc.GetActivityAsync());
        Assert.False(ex is InvalidOperationException,
            $"Read-only guard incorrectly rejected GetActivityAsync for {provider}: {ex?.Message}");
    }

    // ── LOAD / COPY / BULK / OPENROWSET etc. blocklist ───────────────────────

    [Theory]
    [InlineData("LOAD DATA INFILE '/etc/passwd' INTO TABLE users")]
    [InlineData("COPY users FROM '/var/secrets'")]
    [InlineData("load data local infile 'x.csv' into table t")]
    [InlineData("BULK INSERT users FROM 'C:\\data.csv'")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=evil', 'SELECT 1')")]
    [InlineData("SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=evil').db.dbo.tbl")]
    [InlineData("EXEC XP_CMDSHELL 'dir c:\\'")]
    [InlineData("EXEC SP_EXECUTESQL N'DELETE FROM users'")]
    [InlineData("EXEC SP_EXECUTE 1")]
    public async Task ExecuteQueryAsync_DangerousKeywords_ThrowsInvalidOperationException(string sql)
    {
        var svc = CreateService();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ExecuteQueryAsync(sql));
    }

    // ── String literal false-positive guard (W5) ─────────────────────────────

    [Theory]
    [InlineData("SELECT ';DELETE FROM users' AS x")]
    [InlineData("SELECT 'LOAD DATA' AS msg, id FROM t")]
    [InlineData("SELECT 'it''s fine; trust me' AS note")]
    public async Task ExecuteQueryAsync_SemicolonOrDmlInsideStringLiteral_DoesNotThrow(string sql)
    {
        // These are valid read-only queries where semicolons or DML keywords appear
        // only inside string literals. EnsureReadOnly must NOT reject them.
        var svc = CreateService();
        // The query will fail to connect (factory is mocked), but must NOT raise
        // InvalidOperationException from EnsureReadOnly.
        var ex = await Record.ExceptionAsync(() => svc.ExecuteQueryAsync(sql));
        Assert.False(ex is InvalidOperationException,
            $"Read-only guard incorrectly rejected SQL with literal content: {ex?.Message}");
    }
}
