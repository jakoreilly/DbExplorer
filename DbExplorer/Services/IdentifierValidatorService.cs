using Dapper;
using DbExplorer.Core.Interfaces;

namespace DbExplorer.Services;

/// <summary>
/// Validates schema/object identifiers against the live database catalog.
/// All comparisons use parameterized queries — no identifier interpolation here.
/// </summary>
public sealed class IdentifierValidatorService(
    IDbConnectionFactory factory,
    SqlDialect dialect,
    ILogger<IdentifierValidatorService> logger) : IIdentifierValidator
{
    private const int TimeoutSeconds = 10;

    public async Task<bool> SchemaExistsAsync(string schemaName, CancellationToken ct = default)
    {
        dialect.ThrowIfInvalidIdentifier(schemaName, nameof(schemaName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = factory.Provider switch
        {
            DatabaseProvider.SqlServer => "SELECT COUNT(1) FROM sys.schemas WHERE name = @name",
            DatabaseProvider.PostgreSql => "SELECT COUNT(1) FROM information_schema.schemata WHERE schema_name = @name",
            DatabaseProvider.MySql => "SELECT COUNT(1) FROM information_schema.schemata WHERE schema_name = @name",
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var count = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                sql,
                new { name = schemaName },
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        return count > 0;
    }

    public async Task<bool> ObjectExistsAsync(string schemaName, string objectName, CancellationToken ct = default)
    {
        dialect.ThrowIfInvalidIdentifier(schemaName, nameof(schemaName));
        dialect.ThrowIfInvalidIdentifier(objectName, nameof(objectName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
                """
                SELECT COUNT(1)
                FROM sys.objects o
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE s.name = @schema AND o.name = @obj
                AND o.type IN ('U','V','P','FN','TF','IF')
                """,
            DatabaseProvider.PostgreSql or DatabaseProvider.MySql =>
                """
                SELECT COUNT(1)
                FROM (
                    SELECT t.table_schema AS SchemaName, t.table_name AS ObjectName
                    FROM information_schema.tables t
                    WHERE t.table_schema = @schema
                      AND t.table_name = @obj
                      AND t.table_type IN ('BASE TABLE', 'VIEW')

                    UNION ALL

                    SELECT r.routine_schema AS SchemaName, r.routine_name AS ObjectName
                    FROM information_schema.routines r
                    WHERE r.routine_schema = @schema
                      AND r.routine_name = @obj
                      AND r.routine_type IN ('FUNCTION', 'PROCEDURE')
                ) x
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var count = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                sql,
                new { schema = schemaName, obj = objectName },
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        return count > 0;
    }

    public async Task ValidateObjectAsync(string schemaName, string objectName, CancellationToken ct = default)
    {
        if (!await ObjectExistsAsync(schemaName, objectName, ct))
        {
            logger.LogWarning("Validation failed: object {Schema}.{Object} not found in catalog",
                schemaName, objectName);
            throw new InvalidOperationException(
                $"Object '{schemaName}.{objectName}' was not found in the database catalog.");
        }
    }
}
