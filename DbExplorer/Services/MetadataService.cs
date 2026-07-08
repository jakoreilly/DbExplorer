using Dapper;
using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace DbExplorer.Services;

/// <summary>
/// Reads catalog views to enumerate objects and metadata for supported providers.
/// All dynamic identifier usage is validated and quoted via the active SQL dialect.
/// Hot tree lookups (schemas/objects/columns) are cached per connection+catalog with a
/// short TTL; "Refresh" bumps the per-connection version to invalidate.
/// </summary>
public sealed class MetadataService(
    IDbConnectionFactory factory,
    SqlDialect dialect,
    IQueryProfiler profiler,
    ILogger<MetadataService> logger,
    IMemoryCache cache,
    IOptions<MetadataOptions> metadataOptions,
    DatabaseSelectorState selectorState,
    MetadataCacheVersion cacheVersion) : IMetadataService
{
    private const int TimeoutSeconds = 30;

    private Task<T> GetOrQueryAsync<T>(string method, string args, Func<Task<T>> query)
    {
        var ttl = metadataOptions.Value.CacheTtlSeconds;
        if (ttl <= 0)
            return query();

        var connection = selectorState.Current.Name;
        var key = $"meta|{connection}|{factory.Provider}|{selectorState.SelectedCatalog ?? ""}" +
                  $"|v{cacheVersion.Get(connection)}|{method}|{args}";

        return cache.GetOrCreateAsync(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttl);
            return query();
        })!;
    }

    public async Task<string> GetCurrentCatalogAsync(CancellationToken ct = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = factory.Provider switch
        {
            DatabaseProvider.SqlServer => "SELECT DB_NAME()",
            DatabaseProvider.PostgreSql => "SELECT current_database()",
            DatabaseProvider.MySql => "SELECT DATABASE()",
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        return await conn.ExecuteScalarAsync<string>(
            new CommandDefinition(
                sql,
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct)) ?? string.Empty;
    }

    public async Task<IReadOnlyList<CatalogInfo>> GetCatalogsAsync(CancellationToken ct = default)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
                """
                SELECT name
                FROM sys.databases
                WHERE state_desc = 'ONLINE'
                  AND HAS_DBACCESS(name) = 1
                ORDER BY name
                """,
            DatabaseProvider.PostgreSql =>
                """
                SELECT datname
                FROM pg_database
                WHERE datallowconn = TRUE
                  AND datistemplate = FALSE
                ORDER BY datname
                """,
            DatabaseProvider.MySql =>
                """
                SELECT schema_name
                FROM information_schema.schemata
                WHERE schema_name NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
                ORDER BY schema_name
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var rows = await conn.QueryAsync<string>(
            new CommandDefinition(
                sql,
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        return rows
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new CatalogInfo(name))
            .ToList();
    }

    public Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(CancellationToken ct = default)
        => GetOrQueryAsync("schemas", "", () => QuerySchemasAsync(ct));

    private async Task<IReadOnlyList<SchemaInfo>> QuerySchemasAsync(CancellationToken ct)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
                """
                SELECT s.name
                FROM sys.schemas s
                JOIN sys.objects o ON o.schema_id = s.schema_id
                WHERE o.type IN ('U','V','P','FN','TF','IF')
                GROUP BY s.name
                ORDER BY s.name
                """,
            DatabaseProvider.PostgreSql =>
                """
                SELECT n.nspname
                FROM pg_namespace n
                WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
                  AND n.nspname NOT LIKE 'pg_toast%'
                ORDER BY n.nspname
                """,
            DatabaseProvider.MySql =>
                """
                SELECT s.schema_name
                FROM information_schema.schemata s
                WHERE s.schema_name NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
                ORDER BY s.schema_name
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var sw = Stopwatch.StartNew();
        var rows = await conn.QueryAsync<string>(
            new CommandDefinition(
                sql,
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));
        var result = rows.Select(r => new SchemaInfo(r)).ToList();
        sw.Stop();
        logger.LogDebug("GetSchemasAsync [{Provider}] returned {Count} schema(s) in {ElapsedMs}ms",
            factory.Provider, result.Count, sw.ElapsedMilliseconds);
        profiler.Record(factory.Provider.ToString(), sql, sw.ElapsedMilliseconds, result.Count);
        return result;
    }

    public Task<IReadOnlyList<DatabaseObjectInfo>> GetObjectsAsync(
        string? schemaName = null, string? search = null, CancellationToken ct = default)
        => GetOrQueryAsync("objects", $"{schemaName ?? ""}|{search ?? ""}", () => QueryObjectsAsync(schemaName, search, ct));

    private async Task<IReadOnlyList<DatabaseObjectInfo>> QueryObjectsAsync(
        string? schemaName, string? search, CancellationToken ct)
    {
        if (schemaName is not null)
            dialect.ThrowIfInvalidIdentifier(schemaName, nameof(schemaName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
                """
                SELECT
                    s.name  AS SchemaName,
                    o.name  AS ObjectName,
                    CASE o.type
                        WHEN 'U'  THEN 'TABLE'
                        WHEN 'V'  THEN 'VIEW'
                        WHEN 'P'  THEN 'PROCEDURE'
                        WHEN 'FN' THEN 'SCALAR_FUNCTION'
                        WHEN 'TF' THEN 'TABLE_FUNCTION' -- multi-statement table-valued function
                        WHEN 'IF' THEN 'TABLE_FUNCTION' -- inline table-valued function
                        ELSE o.type
                    END AS ObjectType
                FROM sys.objects o
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.type IN ('U','V','P','FN','TF','IF')
                  AND (@schema IS NULL OR s.name = @schema)
                ORDER BY s.name, ObjectType, o.name
                """,
            DatabaseProvider.PostgreSql =>
                """
                SELECT * FROM (
                    SELECT
                        t.table_schema AS "SchemaName",
                        t.table_name AS "ObjectName",
                        CASE t.table_type
                            WHEN 'BASE TABLE' THEN 'TABLE'
                            WHEN 'VIEW' THEN 'VIEW'
                            ELSE t.table_type
                        END AS "ObjectType"
                    FROM information_schema.tables t
                    WHERE t.table_schema NOT IN ('pg_catalog', 'information_schema')
                      AND t.table_type IN ('BASE TABLE', 'VIEW')
                      AND (@schema IS NULL OR t.table_schema = @schema)

                    UNION ALL

                    SELECT
                        r.routine_schema AS "SchemaName",
                        r.routine_name AS "ObjectName",
                        CASE r.routine_type
                            WHEN 'PROCEDURE' THEN 'PROCEDURE'
                            WHEN 'FUNCTION' THEN 'SCALAR_FUNCTION'
                            ELSE r.routine_type
                        END AS "ObjectType"
                    FROM information_schema.routines r
                    WHERE r.routine_schema NOT IN ('pg_catalog', 'information_schema')
                      AND r.routine_type IN ('FUNCTION', 'PROCEDURE')
                      AND (@schema IS NULL OR r.routine_schema = @schema)
                ) x
                ORDER BY "SchemaName", "ObjectType", "ObjectName"
                """,
            DatabaseProvider.MySql =>
                """
                SELECT * FROM (
                    SELECT
                        t.table_schema AS SchemaName,
                        t.table_name AS ObjectName,
                        CASE t.table_type
                            WHEN 'BASE TABLE' THEN 'TABLE'
                            WHEN 'VIEW' THEN 'VIEW'
                            ELSE t.table_type
                        END AS ObjectType
                    FROM information_schema.tables t
                    WHERE t.table_schema NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
                      AND t.table_type IN ('BASE TABLE', 'VIEW')
                      AND (@schema IS NULL OR t.table_schema = @schema)

                    UNION ALL

                    SELECT
                        r.routine_schema AS SchemaName,
                        r.routine_name AS ObjectName,
                        CASE r.routine_type
                            WHEN 'PROCEDURE' THEN 'PROCEDURE'
                            WHEN 'FUNCTION' THEN 'SCALAR_FUNCTION'
                            ELSE r.routine_type
                        END AS ObjectType
                    FROM information_schema.routines r
                    WHERE r.routine_schema NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
                      AND r.routine_type IN ('FUNCTION', 'PROCEDURE')
                      AND (@schema IS NULL OR r.routine_schema = @schema)
                ) x
                ORDER BY SchemaName, ObjectType, ObjectName
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var sw = Stopwatch.StartNew();
        var rows = await conn.QueryAsync(
            new CommandDefinition(sql,
                new { schema = schemaName, search = string.IsNullOrWhiteSpace(search) ? null : search.Trim() },
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        var result = rows.Select(r => new DatabaseObjectInfo(
            (string)r.SchemaName,
            (string)r.ObjectName,
            (string)r.ObjectType)).ToList();
        sw.Stop();
        logger.LogDebug("GetObjectsAsync [{Provider}] schema={Schema} search={Search} returned {Count} object(s) in {ElapsedMs}ms",
            factory.Provider, schemaName ?? "*", string.IsNullOrWhiteSpace(search) ? "*" : search, result.Count, sw.ElapsedMilliseconds);
        profiler.Record(factory.Provider.ToString(), sql, sw.ElapsedMilliseconds, result.Count);
        return result;
    }

    public async Task<long> GetRowCountAsync(
        string schemaName, string tableName, CancellationToken ct = default)
    {
        dialect.ThrowIfInvalidIdentifier(schemaName, nameof(schemaName));
        dialect.ThrowIfInvalidIdentifier(tableName, nameof(tableName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var fullName = $"{dialect.QuoteIdentifier(schemaName)}.{dialect.QuoteIdentifier(tableName)}";
        var sql = factory.Provider == DatabaseProvider.SqlServer
            ? $"SELECT COUNT_BIG(*) FROM {fullName}"
            : $"SELECT COUNT(*) FROM {fullName}";

        return await conn.ExecuteScalarAsync<long>(
            new CommandDefinition(
                sql,
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));
    }

    public Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(
        string schemaName, string objectName, CancellationToken ct = default)
        => GetOrQueryAsync("columns", $"{schemaName}.{objectName}", () => QueryColumnsAsync(schemaName, objectName, ct));

    private async Task<IReadOnlyList<ColumnInfo>> QueryColumnsAsync(
        string schemaName, string objectName, CancellationToken ct)
    {
        dialect.ThrowIfInvalidIdentifier(schemaName, nameof(schemaName));
        dialect.ThrowIfInvalidIdentifier(objectName, nameof(objectName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        // PostgreSQL lowercases unquoted aliases so we must double-quote them.
        var isPg = factory.Provider == DatabaseProvider.PostgreSql;
        var q = isPg ? "\"" : "";

        var sw = Stopwatch.StartNew();
        var rows = await conn.QueryAsync(
            new CommandDefinition(
                $"""
                SELECT
                    c.TABLE_SCHEMA   AS {q}SchemaName{q},
                    c.TABLE_NAME     AS {q}TableName{q},
                    c.COLUMN_NAME    AS {q}ColumnName{q},
                    c.ORDINAL_POSITION AS {q}OrdinalPosition{q},
                    CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS {q}IsNullable{q},
                    c.DATA_TYPE      AS {q}DataType{q},
                    c.CHARACTER_MAXIMUM_LENGTH AS {q}MaxLength{q},
                    c.NUMERIC_PRECISION AS {q}NumericPrecision{q},
                    c.NUMERIC_SCALE  AS {q}NumericScale{q},
                    CASE WHEN kcu.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END AS {q}IsPrimaryKey{q},
                    c.COLUMN_DEFAULT AS {q}DefaultValue{q}
                FROM INFORMATION_SCHEMA.COLUMNS c
                LEFT JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                    ON tc.TABLE_SCHEMA = c.TABLE_SCHEMA
                   AND tc.TABLE_NAME  = c.TABLE_NAME
                   AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                    ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                   AND kcu.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
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

        var result = rows.Select(r => new ColumnInfo(
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
        sw.Stop();
        logger.LogDebug("GetColumnsAsync [{Provider}] {Schema}.{Table} returned {Count} column(s) in {ElapsedMs}ms",
            factory.Provider, schemaName, objectName, result.Count, sw.ElapsedMilliseconds);
        profiler.Record(factory.Provider.ToString(), $"GetColumnsAsync {schemaName}.{objectName}", sw.ElapsedMilliseconds, result.Count);
        return result;
    }

    public async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(
        string schemaName, string tableName, CancellationToken ct = default)
    {
        dialect.ThrowIfInvalidIdentifier(schemaName, nameof(schemaName));
        dialect.ThrowIfInvalidIdentifier(tableName, nameof(tableName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
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
            DatabaseProvider.PostgreSql =>
                """
                SELECT
                    i.schemaname AS "SchemaName",
                    i.tablename AS "TableName",
                    i.indexname AS "IndexName",
                    CASE WHEN i.indexdef ILIKE '% UNIQUE %' THEN 1 ELSE 0 END AS "IsUnique",
                    CASE WHEN i.indexname ILIKE '%pkey' THEN 1 ELSE 0 END AS "IsPrimaryKey",
                    0 AS "IsClustered",
                    TRIM(BOTH ')' FROM SPLIT_PART(SPLIT_PART(i.indexdef, '(', 2), ' INCLUDE', 1)) AS "Columns"
                FROM pg_indexes i
                WHERE i.schemaname = @schema
                  AND i.tablename = @table
                ORDER BY i.indexname
                """,
            DatabaseProvider.MySql =>
                """
                SELECT
                    s.table_schema AS SchemaName,
                    s.table_name AS TableName,
                    s.index_name AS IndexName,
                    CASE WHEN MAX(s.non_unique) = 0 THEN 1 ELSE 0 END AS IsUnique,
                    CASE WHEN s.index_name = 'PRIMARY' THEN 1 ELSE 0 END AS IsPrimaryKey,
                    0 AS IsClustered,
                    GROUP_CONCAT(s.column_name ORDER BY s.seq_in_index SEPARATOR ', ') AS Columns
                FROM information_schema.statistics s
                WHERE s.table_schema = @schema
                  AND s.table_name = @table
                GROUP BY s.table_schema, s.table_name, s.index_name
                ORDER BY s.index_name
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var rows = await conn.QueryAsync(
            new CommandDefinition(
                sql,
                new { schema = schemaName, table = tableName },
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        return rows.Select(r => new IndexInfo(
            (string)r.SchemaName,
            (string)r.TableName,
            (string)r.IndexName,
            Convert.ToInt32(r.IsUnique) == 1,
            Convert.ToInt32(r.IsPrimaryKey) == 1,
            Convert.ToInt32(r.IsClustered) == 1,
            (string)r.Columns)).ToList();
    }

    public async Task<IReadOnlyList<ForeignKeyInfo>> GetForeignKeysAsync(
        string schemaName, string tableName, CancellationToken ct = default)
    {
        dialect.ThrowIfInvalidIdentifier(schemaName, nameof(schemaName));
        dialect.ThrowIfInvalidIdentifier(tableName, nameof(tableName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
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
            DatabaseProvider.PostgreSql =>
                """
                SELECT
                    tc.constraint_name AS "ConstraintName",
                    kcu.table_schema AS "SchemaName",
                    kcu.table_name AS "TableName",
                    kcu.column_name AS "ColumnName",
                    ccu.table_schema AS "ReferencedSchema",
                    ccu.table_name AS "ReferencedTable",
                    ccu.column_name AS "ReferencedColumn"
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                    ON kcu.constraint_name = tc.constraint_name
                   AND kcu.constraint_schema = tc.constraint_schema
                LEFT JOIN information_schema.constraint_column_usage ccu
                    ON ccu.constraint_name = tc.constraint_name
                   AND ccu.constraint_schema = tc.constraint_schema
                WHERE tc.constraint_type = 'FOREIGN KEY'
                  AND kcu.table_schema = @schema
                  AND kcu.table_name = @table
                ORDER BY tc.constraint_name, kcu.ordinal_position
                """,
                        DatabaseProvider.MySql =>
                                """
                                SELECT
                                        kcu.constraint_name AS ConstraintName,
                                        kcu.table_schema AS SchemaName,
                                        kcu.table_name AS TableName,
                                        kcu.column_name AS ColumnName,
                                        kcu.referenced_table_schema AS ReferencedSchema,
                                        kcu.referenced_table_name AS ReferencedTable,
                                        kcu.referenced_column_name AS ReferencedColumn
                                FROM information_schema.key_column_usage kcu
                                WHERE kcu.table_schema = @schema
                                    AND kcu.table_name = @table
                                    AND kcu.referenced_table_name IS NOT NULL
                                ORDER BY kcu.constraint_name, kcu.ordinal_position
                                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var rows = await conn.QueryAsync(
            new CommandDefinition(
                sql,
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

    public Task<IReadOnlyList<ColumnSearchHit>> GetAllColumnsAsync(CancellationToken ct = default)
        => GetOrQueryAsync("allcolumns", "", () => QueryAllColumnsAsync(ct));

    private async Task<IReadOnlyList<ColumnSearchHit>> QueryAllColumnsAsync(CancellationToken ct)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
                """
                SELECT c.TABLE_SCHEMA AS SchemaName,
                       c.TABLE_NAME   AS TableName,
                       c.COLUMN_NAME  AS ColumnName
                FROM INFORMATION_SCHEMA.COLUMNS c
                ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
                """,
            DatabaseProvider.PostgreSql =>
                """
                SELECT c.table_schema AS "SchemaName",
                       c.table_name   AS "TableName",
                       c.column_name  AS "ColumnName"
                FROM information_schema.columns c
                WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema')
                ORDER BY c.table_schema, c.table_name, c.ordinal_position
                """,
            DatabaseProvider.MySql =>
                """
                SELECT c.table_schema AS SchemaName,
                       c.table_name   AS TableName,
                       c.column_name  AS ColumnName
                FROM information_schema.columns c
                WHERE c.table_schema = DATABASE()
                ORDER BY c.table_schema, c.table_name, c.ordinal_position
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = await conn.QueryAsync(
            new CommandDefinition(
                sql,
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        var result = rows.Select(r => new ColumnSearchHit(
            (string)r.SchemaName,
            (string)r.TableName,
            (string)r.ColumnName)).ToList();

        profiler.Record(factory.Provider.ToString(), "GetAllColumnsAsync (autocomplete metadata)", sw.ElapsedMilliseconds, result.Count);
        return result;
    }

    public Task<IReadOnlyList<ForeignKeyInfo>> GetAllForeignKeysAsync(
        string schemaName, CancellationToken ct = default)
        => GetOrQueryAsync("allfks", schemaName, () => QueryAllForeignKeysAsync(schemaName, ct));

    private async Task<IReadOnlyList<ForeignKeyInfo>> QueryAllForeignKeysAsync(
        string schemaName, CancellationToken ct)
    {
        dialect.ThrowIfInvalidIdentifier(schemaName, nameof(schemaName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
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
                WHERE ps.name = @schema
                ORDER BY pt.name, fk.name
                """,
            DatabaseProvider.PostgreSql =>
                """
                SELECT
                    tc.constraint_name AS "ConstraintName",
                    kcu.table_schema AS "SchemaName",
                    kcu.table_name AS "TableName",
                    kcu.column_name AS "ColumnName",
                    ccu.table_schema AS "ReferencedSchema",
                    ccu.table_name AS "ReferencedTable",
                    ccu.column_name AS "ReferencedColumn"
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                    ON kcu.constraint_name = tc.constraint_name
                   AND kcu.constraint_schema = tc.constraint_schema
                JOIN information_schema.constraint_column_usage ccu
                    ON ccu.constraint_name = tc.constraint_name
                   AND ccu.constraint_schema = tc.constraint_schema
                WHERE tc.constraint_type = 'FOREIGN KEY'
                  AND kcu.table_schema = @schema
                ORDER BY kcu.table_name, tc.constraint_name, kcu.ordinal_position
                """,
            DatabaseProvider.MySql =>
                """
                SELECT
                    kcu.constraint_name AS ConstraintName,
                    kcu.table_schema AS SchemaName,
                    kcu.table_name AS TableName,
                    kcu.column_name AS ColumnName,
                    kcu.referenced_table_schema AS ReferencedSchema,
                    kcu.referenced_table_name AS ReferencedTable,
                    kcu.referenced_column_name AS ReferencedColumn
                FROM information_schema.key_column_usage kcu
                WHERE kcu.table_schema = @schema
                    AND kcu.referenced_table_name IS NOT NULL
                ORDER BY kcu.table_name, kcu.constraint_name, kcu.ordinal_position
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var rows = await conn.QueryAsync(
            new CommandDefinition(
                sql,
                new { schema = schemaName },
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

    public Task<IReadOnlyList<ForeignKeyInfo>> GetCatalogForeignKeysAsync(CancellationToken ct = default)
        => GetOrQueryAsync("catalogfks", "", () => QueryCatalogForeignKeysAsync(ct));

    private async Task<IReadOnlyList<ForeignKeyInfo>> QueryCatalogForeignKeysAsync(CancellationToken ct)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
                """
                SELECT
                    fk.name AS ConstraintName,
                    ps.name AS SchemaName,
                    pt.name AS TableName,
                    pc.name AS ColumnName,
                    rs.name AS ReferencedSchema,
                    rt.name AS ReferencedTable,
                    rc.name AS ReferencedColumn
                FROM sys.foreign_keys fk
                JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
                JOIN sys.tables  pt ON pt.object_id = fk.parent_object_id
                JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
                JOIN sys.columns pc ON pc.object_id = fk.parent_object_id AND pc.column_id = fkc.parent_column_id
                JOIN sys.tables  rt ON rt.object_id = fk.referenced_object_id
                JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
                JOIN sys.columns rc ON rc.object_id = fk.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                ORDER BY ps.name, pt.name, fk.name, fkc.constraint_column_id
                """,
            DatabaseProvider.PostgreSql =>
                """
                SELECT
                    con.conname          AS "ConstraintName",
                    nsc.nspname          AS "SchemaName",
                    child.relname        AS "TableName",
                    ac.attname           AS "ColumnName",
                    nsp.nspname          AS "ReferencedSchema",
                    parent.relname       AS "ReferencedTable",
                    ap.attname           AS "ReferencedColumn"
                FROM pg_constraint con
                JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY
                     AS cols(child_attnum, parent_attnum, ord) ON TRUE
                JOIN pg_class child      ON child.oid  = con.conrelid
                JOIN pg_namespace nsc    ON nsc.oid    = child.relnamespace
                JOIN pg_class parent     ON parent.oid = con.confrelid
                JOIN pg_namespace nsp    ON nsp.oid    = parent.relnamespace
                JOIN pg_attribute ac     ON ac.attrelid = con.conrelid  AND ac.attnum = cols.child_attnum
                JOIN pg_attribute ap     ON ap.attrelid = con.confrelid AND ap.attnum = cols.parent_attnum
                WHERE con.contype = 'f'
                  AND nsc.nspname NOT IN ('pg_catalog', 'information_schema')
                  AND nsc.nspname NOT LIKE 'pg_toast%'
                ORDER BY nsc.nspname, child.relname, con.conname, cols.ord
                """,
            DatabaseProvider.MySql =>
                """
                SELECT
                    kcu.constraint_name          AS ConstraintName,
                    kcu.table_schema             AS SchemaName,
                    kcu.table_name               AS TableName,
                    kcu.column_name              AS ColumnName,
                    kcu.referenced_table_schema  AS ReferencedSchema,
                    kcu.referenced_table_name    AS ReferencedTable,
                    kcu.referenced_column_name   AS ReferencedColumn
                FROM information_schema.referential_constraints rc
                JOIN information_schema.key_column_usage kcu
                  ON kcu.constraint_schema = rc.constraint_schema
                 AND kcu.constraint_name   = rc.constraint_name
                 AND kcu.table_name        = rc.table_name
                WHERE kcu.table_schema = DATABASE()
                  AND kcu.referenced_table_name IS NOT NULL
                ORDER BY kcu.table_name, kcu.constraint_name, kcu.ordinal_position
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var rows = await conn.QueryAsync(
            new CommandDefinition(
                sql,
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
        dialect.ThrowIfInvalidIdentifier(schemaName, nameof(schemaName));
        dialect.ThrowIfInvalidIdentifier(objectName, nameof(objectName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
                """
                SELECT
                    s.name AS SchemaName,
                    o.name AS ObjectName,
                    CASE o.type
                        WHEN 'V'  THEN 'VIEW'
                        WHEN 'P'  THEN 'PROCEDURE'
                        WHEN 'FN' THEN 'SCALAR_FUNCTION'
                        WHEN 'TF' THEN 'TABLE_FUNCTION' -- multi-statement table-valued function
                        WHEN 'IF' THEN 'TABLE_FUNCTION' -- inline table-valued function
                        ELSE o.type
                    END AS ObjectType,
                    m.definition AS Definition
                FROM sys.objects o
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
                WHERE s.name = @schema AND o.name = @obj
                  AND o.type IN ('V','P','FN','TF','IF')
                """,
            DatabaseProvider.PostgreSql =>
                """
                SELECT * FROM (
                    SELECT
                        v.table_schema AS "SchemaName",
                        v.table_name AS "ObjectName",
                        'VIEW' AS "ObjectType",
                        v.view_definition AS "Definition"
                    FROM information_schema.views v
                    WHERE v.table_schema = @schema AND v.table_name = @obj

                    UNION ALL

                    SELECT
                        n.nspname AS "SchemaName",
                        p.proname AS "ObjectName",
                        CASE p.prokind
                            WHEN 'p' THEN 'PROCEDURE'
                            ELSE 'SCALAR_FUNCTION'
                        END AS "ObjectType",
                        pg_get_functiondef(p.oid) AS "Definition"
                    FROM pg_proc p
                    JOIN pg_namespace n ON n.oid = p.pronamespace
                    WHERE n.nspname = @schema AND p.proname = @obj
                ) x
                LIMIT 1
                """,
            DatabaseProvider.MySql =>
                """
                SELECT * FROM (
                    SELECT
                        v.table_schema AS SchemaName,
                        v.table_name AS ObjectName,
                        'VIEW' AS ObjectType,
                        v.view_definition AS Definition
                    FROM information_schema.views v
                    WHERE v.table_schema = @schema AND v.table_name = @obj

                    UNION ALL

                    SELECT
                        r.routine_schema AS SchemaName,
                        r.routine_name AS ObjectName,
                        CASE r.routine_type
                            WHEN 'PROCEDURE' THEN 'PROCEDURE'
                            ELSE 'SCALAR_FUNCTION'
                        END AS ObjectType,
                        r.routine_definition AS Definition
                    FROM information_schema.routines r
                    WHERE r.routine_schema = @schema AND r.routine_name = @obj
                ) x
                LIMIT 1
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var row = await conn.QueryFirstOrDefaultAsync(
            new CommandDefinition(
                sql,
                new { schema = schemaName, obj = objectName },
                commandTimeout: TimeoutSeconds,
                cancellationToken: ct));

        if (row is null)
        {
            logger.LogDebug("No definition found for {Schema}.{Object}", schemaName, objectName);
            return null;
        }

        return new ObjectDefinition(
            (string)row.SchemaName,
            (string)row.ObjectName,
            (string)row.ObjectType,
            (string?)row.Definition);
    }

    public Task<SearchResult> SearchAsync(string term, CancellationToken ct = default)
        => GetOrQueryAsync("search", term, () => QuerySearchAsync(term, ct));

    private async Task<SearchResult> QuerySearchAsync(string term, CancellationToken ct)
    {
        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var like = $"%{term}%";
        var sw = Stopwatch.StartNew();

        var objectSql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
                """
                SELECT TOP 50
                    s.name AS SchemaName,
                    o.name AS ObjectName,
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
                  AND o.name LIKE @term
                ORDER BY o.name
                """,
            DatabaseProvider.PostgreSql =>
                """
                SELECT * FROM (
                    SELECT
                        t.table_schema AS "SchemaName",
                        t.table_name AS "ObjectName",
                        CASE t.table_type WHEN 'BASE TABLE' THEN 'TABLE' WHEN 'VIEW' THEN 'VIEW' ELSE t.table_type END AS "ObjectType"
                    FROM information_schema.tables t
                    WHERE t.table_schema NOT IN ('pg_catalog', 'information_schema')
                      AND t.table_type IN ('BASE TABLE', 'VIEW')
                      AND t.table_name LIKE @term

                    UNION ALL

                    SELECT
                        r.routine_schema AS "SchemaName",
                        r.routine_name AS "ObjectName",
                        CASE r.routine_type WHEN 'PROCEDURE' THEN 'PROCEDURE' WHEN 'FUNCTION' THEN 'SCALAR_FUNCTION' ELSE r.routine_type END AS "ObjectType"
                    FROM information_schema.routines r
                    WHERE r.routine_schema NOT IN ('pg_catalog', 'information_schema')
                      AND r.routine_type IN ('FUNCTION', 'PROCEDURE')
                      AND r.routine_name LIKE @term
                ) x
                ORDER BY "ObjectName"
                LIMIT 50
                """,
            DatabaseProvider.MySql =>
                """
                SELECT * FROM (
                    SELECT
                        t.table_schema AS SchemaName,
                        t.table_name AS ObjectName,
                        CASE t.table_type WHEN 'BASE TABLE' THEN 'TABLE' WHEN 'VIEW' THEN 'VIEW' ELSE t.table_type END AS ObjectType
                    FROM information_schema.tables t
                    WHERE t.table_schema NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
                      AND t.table_type IN ('BASE TABLE', 'VIEW')
                      AND t.table_name LIKE @term

                    UNION ALL

                    SELECT
                        r.routine_schema AS SchemaName,
                        r.routine_name AS ObjectName,
                        CASE r.routine_type WHEN 'PROCEDURE' THEN 'PROCEDURE' WHEN 'FUNCTION' THEN 'SCALAR_FUNCTION' ELSE r.routine_type END AS ObjectType
                    FROM information_schema.routines r
                    WHERE r.routine_schema NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
                      AND r.routine_type IN ('FUNCTION', 'PROCEDURE')
                      AND r.routine_name LIKE @term
                ) x
                ORDER BY ObjectName
                LIMIT 50
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var isPg = factory.Provider == DatabaseProvider.PostgreSql;
        var q = isPg ? "\"" : "";
        var columnSql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
                """
                SELECT TOP 50
                    s.name AS SchemaName,
                    o.name AS TableName,
                    c.name AS ColumnName
                FROM sys.columns c
                JOIN sys.objects o ON c.object_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.type IN ('U','V')
                  AND c.name LIKE @term
                ORDER BY c.name
                """,
            DatabaseProvider.PostgreSql or DatabaseProvider.MySql =>
                $"""
                SELECT
                    c.table_schema AS {q}SchemaName{q},
                    c.table_name AS {q}TableName{q},
                    c.column_name AS {q}ColumnName{q}
                FROM information_schema.columns c
                WHERE c.column_name LIKE @term
                ORDER BY c.column_name
                LIMIT 50
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var objectRows = await conn.QueryAsync(
            new CommandDefinition(objectSql, new { term = like }, commandTimeout: TimeoutSeconds, cancellationToken: ct));
        var columnRows = await conn.QueryAsync(
            new CommandDefinition(columnSql, new { term = like }, commandTimeout: TimeoutSeconds, cancellationToken: ct));

        var objects = objectRows.Select(r => new DatabaseObjectInfo(
            (string)r.SchemaName, (string)r.ObjectName, (string)r.ObjectType)).ToList();
        var columns = columnRows.Select(r => new ColumnSearchHit(
            (string)r.SchemaName, (string)r.TableName, (string)r.ColumnName)).ToList();

        sw.Stop();
        logger.LogDebug("SearchAsync [{Provider}] term={Term} returned {ObjectCount} object(s), {ColumnCount} column(s) in {ElapsedMs}ms",
            factory.Provider, term, objects.Count, columns.Count, sw.ElapsedMilliseconds);
        profiler.Record(factory.Provider.ToString(), $"SearchAsync '{term}'", sw.ElapsedMilliseconds, objects.Count + columns.Count);

        return new SearchResult(objects, columns);
    }

    public async Task<IReadOnlyList<TriggerInfo>> GetTriggersAsync(
        string schemaName, string tableName, CancellationToken ct = default)
    {
        dialect.ThrowIfInvalidIdentifier(schemaName, nameof(schemaName));
        dialect.ThrowIfInvalidIdentifier(tableName, nameof(tableName));

        await using var conn = factory.Create();
        await conn.OpenAsync(ct);

        var sql = factory.Provider switch
        {
            DatabaseProvider.SqlServer =>
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
            DatabaseProvider.PostgreSql =>
                """
                SELECT
                    n.nspname AS "SchemaName",
                    c.relname AS "TableName",
                    tg.tgname AS "TriggerName",
                    CASE WHEN tg.tgenabled = 'D' THEN 1 ELSE 0 END AS "IsDisabled",
                    TRIM(BOTH ', ' FROM CONCAT(
                        CASE WHEN (tg.tgtype & 4) <> 0 THEN 'INSERT, ' ELSE '' END,
                        CASE WHEN (tg.tgtype & 8) <> 0 THEN 'DELETE, ' ELSE '' END,
                        CASE WHEN (tg.tgtype & 16) <> 0 THEN 'UPDATE, ' ELSE '' END,
                        CASE WHEN (tg.tgtype & 32) <> 0 THEN 'TRUNCATE, ' ELSE '' END
                    )) AS "Events"
                FROM pg_trigger tg
                JOIN pg_class c ON c.oid = tg.tgrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE tg.tgisinternal = FALSE
                  AND n.nspname = @schema
                  AND c.relname = @table
                ORDER BY tg.tgname
                """,
            DatabaseProvider.MySql =>
                """
                SELECT
                    t.trigger_schema AS SchemaName,
                    t.event_object_table AS TableName,
                    t.trigger_name AS TriggerName,
                    0 AS IsDisabled,
                    t.event_manipulation AS Events
                FROM information_schema.triggers t
                WHERE t.trigger_schema = @schema
                  AND t.event_object_table = @table
                ORDER BY t.trigger_name
                """,
            _ => throw new InvalidOperationException($"Unsupported provider '{factory.Provider}'.")
        };

        var rows = await conn.QueryAsync(
            new CommandDefinition(
                sql,
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
