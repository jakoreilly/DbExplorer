using Dapper;
using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Validation;

namespace DbExplorer.Services;

/// <summary>
/// Validates schema/object identifiers against the live SQL Server catalog.
/// All comparisons use parameterized queries — no identifier interpolation here.
/// </summary>
public sealed class IdentifierValidatorService(
    SqlConnectionFactory factory,
    ILogger<IdentifierValidatorService> logger) : IIdentifierValidator
{
    private const int TimeoutSeconds = 10;

    public async Task<bool> SchemaExistsAsync(string schemaName, CancellationToken ct = default)
    {
        SqlIdentifierHelper.ThrowIfInvalidFormat(schemaName, nameof(schemaName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var count = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(1) FROM sys.schemas WHERE name = @name",
                new { name = schemaName },
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        return count > 0;
    }

    public async Task<bool> ObjectExistsAsync(string schemaName, string objectName, CancellationToken ct = default)
    {
        SqlIdentifierHelper.ThrowIfInvalidFormat(schemaName, nameof(schemaName));
        SqlIdentifierHelper.ThrowIfInvalidFormat(objectName, nameof(objectName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var count = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                SELECT COUNT(1)
                FROM sys.objects o
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE s.name = @schema AND o.name = @obj
                AND o.type IN ('U','V','P','FN','TF','IF')
                """,
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
