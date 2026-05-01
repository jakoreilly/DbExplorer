using Dapper;
using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Core.Validation;

namespace DbExplorer.Services;

/// <summary>
/// Reads SQL Server catalog views to enumerate objects and their metadata.
/// All object name interpolation uses bracket-quoted, catalog-validated identifiers.
/// </summary>
public sealed class MetadataService(
    SqlConnectionFactory factory,
    ILogger<MetadataService> logger) : IMetadataService
{
    private const int TimeoutSeconds = 30;

    public async Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(CancellationToken ct = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync<string>(
            new CommandDefinition(
                """
                SELECT s.name
                FROM sys.schemas s
                JOIN sys.objects o ON o.schema_id = s.schema_id
                WHERE o.type IN ('U','V','P','FN','TF','IF')
                GROUP BY s.name
                ORDER BY s.name
                """,
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        return rows.Select(r => new SchemaInfo(r)).ToList();
    }

    public async Task<IReadOnlyList<DatabaseObjectInfo>> GetObjectsAsync(
        string? schemaName = null, CancellationToken ct = default)
    {
        if (schemaName is not null)
            SqlIdentifierHelper.ThrowIfInvalidFormat(schemaName, nameof(schemaName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = """
            SELECT
                s.name  AS SchemaName,
                o.name  AS ObjectName,
                CASE o.type
                    WHEN 'U'  THEN 'TABLE'
                    WHEN 'V'  THEN 'VIEW'
                    WHEN 'P'  THEN 'PROCEDURE'
                    WHEN 'FN' THEN 'SCALAR_FUNCTION'
                    WHEN 'TF' THEN 'TABLE_FUNCTION'
                    WHEN 'IF' THEN 'TABLE_FUNCTION'
                    ELSE o.type
                END AS ObjectType
            FROM sys.objects o
            JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.type IN ('U','V','P','FN','TF','IF')
              AND (@schema IS NULL OR s.name = @schema)
            ORDER BY s.name, ObjectType, o.name
            """;

        var rows = await conn.QueryAsync(
            new CommandDefinition(sql,
                new { schema = schemaName },
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        return rows.Select(r => new DatabaseObjectInfo(
            (string)r.SchemaName,
            (string)r.ObjectName,
            (string)r.ObjectType)).ToList();
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(
        string schemaName, string objectName, CancellationToken ct = default)
    {
        SqlIdentifierHelper.ThrowIfInvalidFormat(schemaName, nameof(schemaName));
        SqlIdentifierHelper.ThrowIfInvalidFormat(objectName, nameof(objectName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync(
            new CommandDefinition(
                """
                SELECT
                    c.TABLE_SCHEMA   AS SchemaName,
                    c.TABLE_NAME     AS TableName,
                    c.COLUMN_NAME    AS ColumnName,
                    c.ORDINAL_POSITION AS OrdinalPosition,
                    CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS IsNullable,
                    c.DATA_TYPE      AS DataType,
                    c.CHARACTER_MAXIMUM_LENGTH AS MaxLength,
                    c.NUMERIC_PRECISION AS NumericPrecision,
                    c.NUMERIC_SCALE  AS NumericScale,
                    CASE WHEN kcu.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END AS IsPrimaryKey,
                    c.COLUMN_DEFAULT AS DefaultValue
                FROM INFORMATION_SCHEMA.COLUMNS c
                LEFT JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                    ON tc.TABLE_SCHEMA = c.TABLE_SCHEMA
                   AND tc.TABLE_NAME  = c.TABLE_NAME
                   AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                    ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                   AND kcu.TABLE_SCHEMA    = c.TABLE_SCHEMA
                   AND kcu.TABLE_NAME      = c.TABLE_NAME
                   AND kcu.COLUMN_NAME     = c.COLUMN_NAME
                WHERE c.TABLE_SCHEMA = @schema
                  AND c.TABLE_NAME   = @table
                ORDER BY c.ORDINAL_POSITION
                """,
                new { schema = schemaName, table = objectName },
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        return rows.Select(r => new ColumnInfo(
            (string)r.SchemaName,
            (string)r.TableName,
            (string)r.ColumnName,
            (int)r.OrdinalPosition,
            ((int)r.IsNullable) == 1,
            (string)r.DataType,
            (int?)r.MaxLength,
            (int?)r.NumericPrecision,
            (int?)r.NumericScale,
            ((int)r.IsPrimaryKey) == 1,
            (string?)r.DefaultValue)).ToList();
    }

    public async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(
        string schemaName, string tableName, CancellationToken ct = default)
    {
        SqlIdentifierHelper.ThrowIfInvalidFormat(schemaName, nameof(schemaName));
        SqlIdentifierHelper.ThrowIfInvalidFormat(tableName, nameof(tableName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync(
            new CommandDefinition(
                """
                SELECT
                    s.name AS SchemaName,
                    t.name AS TableName,
                    i.name AS IndexName,
                    i.is_unique    AS IsUnique,
                    i.is_primary_key AS IsPrimaryKey,
                    CASE i.type WHEN 1 THEN 1 ELSE 0 END AS IsClustered,
                    STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) AS Columns
                FROM sys.indexes i
                JOIN sys.tables t  ON i.object_id = t.object_id
                JOIN sys.schemas s ON t.schema_id = s.schema_id
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c  ON c.object_id = i.object_id AND c.column_id = ic.column_id
                WHERE s.name = @schema AND t.name = @table
                  AND i.name IS NOT NULL
                GROUP BY s.name, t.name, i.name, i.is_unique, i.is_primary_key, i.type
                ORDER BY i.is_primary_key DESC, i.name
                """,
                new { schema = schemaName, table = tableName },
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        return rows.Select(r => new IndexInfo(
            (string)r.SchemaName,
            (string)r.TableName,
            (string)r.IndexName,
            (bool)r.IsUnique,
            (bool)r.IsPrimaryKey,
            ((int)r.IsClustered) == 1,
            (string)r.Columns)).ToList();
    }

    public async Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(
        string schemaName, string tableName, CancellationToken ct = default)
    {
        SqlIdentifierHelper.ThrowIfInvalidFormat(schemaName, nameof(schemaName));
        SqlIdentifierHelper.ThrowIfInvalidFormat(tableName, nameof(tableName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync(
            new CommandDefinition(
                """
                SELECT
                    fk.name          AS ConstraintName,
                    ps.name          AS SchemaName,
                    pt.name          AS TableName,
                    pc.name          AS ColumnName,
                    rs.name          AS ReferencedSchema,
                    rt.name          AS ReferencedTable,
                    rc.name          AS ReferencedColumn
                FROM sys.foreign_keys fk
                JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
                JOIN sys.tables  pt ON pt.object_id = fk.parent_object_id
                JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
                JOIN sys.columns pc ON pc.object_id = fk.parent_object_id AND pc.column_id = fkc.parent_column_id
                JOIN sys.tables  rt ON rt.object_id = fk.referenced_object_id
                JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
                JOIN sys.columns rc ON rc.object_id = fk.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                WHERE ps.name = @schema AND pt.name = @table
                ORDER BY fk.name
                """,
                new { schema = schemaName, table = tableName },
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        return rows.Select(r => new ForeignKeyInfo(
            (string)r.ConstraintName,
            (string)r.SchemaName,
            (string)r.TableName,
            (string)r.ColumnName,
            (string)r.ReferencedSchema,
            (string)r.ReferencedTable,
            (string)r.ReferencedColumn)).ToList();
    }

    public async Task<ObjectDefinition?> GetObjectDefinitionAsync(
        string schemaName, string objectName, CancellationToken ct = default)
    {
        SqlIdentifierHelper.ThrowIfInvalidFormat(schemaName, nameof(schemaName));
        SqlIdentifierHelper.ThrowIfInvalidFormat(objectName, nameof(objectName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var row = await conn.QueryFirstOrDefaultAsync(
            new CommandDefinition(
                """
                SELECT
                    s.name AS SchemaName,
                    o.name AS ObjectName,
                    CASE o.type
                        WHEN 'V'  THEN 'VIEW'
                        WHEN 'P'  THEN 'PROCEDURE'
                        WHEN 'FN' THEN 'SCALAR_FUNCTION'
                        WHEN 'TF' THEN 'TABLE_FUNCTION'
                        WHEN 'IF' THEN 'TABLE_FUNCTION'
                        ELSE o.type
                    END AS ObjectType,
                    m.definition AS Definition
                FROM sys.objects o
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
                WHERE s.name = @schema AND o.name = @obj
                  AND o.type IN ('V','P','FN','TF','IF')
                """,
                new { schema = schemaName, obj = objectName },
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        if (row is null) return null;

        return new ObjectDefinition(
            (string)row.SchemaName,
            (string)row.ObjectName,
            (string)row.ObjectType,
            (string?)row.Definition);
    }

    public async Task<IReadOnlyList<TriggerInfo>> GetTriggersAsync(
        string schemaName, string tableName, CancellationToken ct = default)
    {
        SqlIdentifierHelper.ThrowIfInvalidFormat(schemaName, nameof(schemaName));
        SqlIdentifierHelper.ThrowIfInvalidFormat(tableName, nameof(tableName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var rows = await conn.QueryAsync(
            new CommandDefinition(
                """
                SELECT
                    s.name  AS SchemaName,
                    t.name  AS TableName,
                    tr.name AS TriggerName,
                    tr.is_disabled AS IsDisabled,
                    STRING_AGG(
                        CASE te.type_desc
                            WHEN 'INSERT' THEN 'INSERT'
                            WHEN 'UPDATE' THEN 'UPDATE'
                            WHEN 'DELETE' THEN 'DELETE'
                        END, ', '
                    ) WITHIN GROUP (ORDER BY te.type_desc) AS Events
                FROM sys.triggers tr
                JOIN sys.tables t  ON tr.parent_id = t.object_id
                JOIN sys.schemas s ON t.schema_id  = s.schema_id
                JOIN sys.trigger_events te ON te.object_id = tr.object_id
                WHERE s.name = @schema AND t.name = @table
                GROUP BY s.name, t.name, tr.name, tr.is_disabled
                ORDER BY tr.name
                """,
                new { schema = schemaName, table = tableName },
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        return rows.Select(r => new TriggerInfo(
            (string)r.SchemaName,
            (string)r.TableName,
            (string)r.TriggerName,
            !((bool)r.IsDisabled),
            (string)r.Events)).ToList();
    }
}
