using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DbExplorer.Core.Interfaces;
using ModelContextProtocol.Server;

namespace DbExplorer.Services;

/// <summary>
/// Read-only MCP tools for DbExplorer.
/// All methods are stateless and rely on the DI-provided services, which are scoped per request.
/// </summary>
[McpServerToolType]
public sealed class DbExplorerMcpTools(IMetadataService metadata, IAdHocQueryService adhoc)
{
    // ── Schema / object discovery ─────────────────────────────────────────────

    [McpServerTool]
    [Description("List all schemas in the current database connection.")]
    public async Task<string> ListSchemas(CancellationToken ct = default)
    {
        var schemas = await metadata.GetSchemasAsync(ct);
        if (!schemas.Any()) return "No schemas found.";
        return string.Join("\n", schemas.Select(s => s.SchemaName));
    }

    [McpServerTool]
    [Description(
        "List all objects (tables, views, stored procedures, functions) in a schema. " +
        "Pass an empty schema to list objects across all schemas.")]
    public async Task<string> ListObjects(
        [Description("Schema name (e.g. 'public', 'dbo'). Leave empty for all schemas.")] string schema,
        CancellationToken ct = default)
    {
        var objects = await metadata.GetObjectsAsync(string.IsNullOrWhiteSpace(schema) ? null : schema, ct);
        if (!objects.Any()) return "No objects found.";

        var sb = new StringBuilder();
        foreach (var g in objects.GroupBy(o => o.ObjectType).OrderBy(g => g.Key))
        {
            sb.AppendLine($"--- {g.Key} ---");
            foreach (var o in g.OrderBy(o => o.SchemaName).ThenBy(o => o.ObjectName))
                sb.AppendLine($"  {o.SchemaName}.{o.ObjectName}");
        }
        return sb.ToString();
    }

    // ── Column / index / FK metadata ─────────────────────────────────────────

    [McpServerTool]
    [Description("Get column metadata for a table or view, including data types, nullability, and primary key membership.")]
    public async Task<string> GetColumns(
        [Description("Schema name (e.g. 'public', 'dbo')")] string schema,
        [Description("Table or view name")] string objectName,
        CancellationToken ct = default)
    {
        var cols = await metadata.GetColumnsAsync(schema, objectName, ct);
        if (!cols.Any()) return "No columns found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Columns for {schema}.{objectName}:");
        foreach (var c in cols.OrderBy(c => c.OrdinalPosition))
        {
            var pk = c.IsPrimaryKey ? " [PK]" : "";
            var nullable = c.IsNullable ? " NULL" : " NOT NULL";
            var len = c.MaxLength.HasValue ? $"({c.MaxLength})" : "";
            sb.AppendLine($"  {c.ColumnName}: {c.DataType}{len}{nullable}{pk}");
        }
        return sb.ToString();
    }

    [McpServerTool]
    [Description("Get index definitions for a table.")]
    public async Task<string> GetIndexes(
        [Description("Schema name")] string schema,
        [Description("Table name")] string tableName,
        CancellationToken ct = default)
    {
        var indexes = await metadata.GetIndexesAsync(schema, tableName, ct);
        if (!indexes.Any()) return "No indexes found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Indexes for {schema}.{tableName}:");
        foreach (var idx in indexes)
        {
            var unique = idx.IsUnique ? "UNIQUE " : "";
            var pk = idx.IsPrimaryKey ? "[PK] " : "";
            sb.AppendLine($"  {pk}{unique}{idx.IndexName} ({idx.Columns})");
        }
        return sb.ToString();
    }

    [McpServerTool]
    [Description("Get foreign key relationships for a table.")]
    public async Task<string> GetForeignKeys(
        [Description("Schema name")] string schema,
        [Description("Table name")] string tableName,
        CancellationToken ct = default)
    {
        var fks = await metadata.GetForeignKeysAsync(schema, tableName, ct);
        if (!fks.Any()) return "No foreign keys found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Foreign keys for {schema}.{tableName}:");
        foreach (var fk in fks)
            sb.AppendLine($"  {fk.ConstraintName}: {fk.ColumnName} → {fk.ReferencedSchema}.{fk.ReferencedTable}({fk.ReferencedColumn})");
        return sb.ToString();
    }

    [McpServerTool]
    [Description("Get the source definition (DDL) for a view, stored procedure, or function.")]
    public async Task<string> GetDefinition(
        [Description("Schema name")] string schema,
        [Description("Object name (view, procedure, or function)")] string objectName,
        CancellationToken ct = default)
    {
        var def = await metadata.GetObjectDefinitionAsync(schema, objectName, ct);
        if (def is null) return $"No definition found for {schema}.{objectName}.";
        return def.Definition ?? $"Definition for {schema}.{objectName} is empty.";
    }

    // ── Query execution ───────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Execute a read-only SELECT query against the current database. " +
        "Only SELECT, WITH (CTE), SHOW, EXPLAIN, DESCRIBE, and DESC statements are allowed. " +
        "Results are returned as a JSON array of row objects. Maximum 500 rows returned.")]
    public async Task<string> RunSelectQuery(
        [Description("A read-only SQL SELECT statement to execute.")] string sql,
        CancellationToken ct = default)
    {
        var result = await adhoc.ExecuteQueryAsync(sql, maxRows: 500, ct);

        if (result.Warning is not null && result.Rows.Count == 0)
            return $"Warning: {result.Warning}";

        var rows = result.Rows.Select(row =>
        {
            var obj = new Dictionary<string, object?>();
            foreach (var col in result.Columns)
                obj[col.Name] = row.TryGetValue(col.Name, out var v) ? v : null;
            return obj;
        }).ToList();

        var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = false });

        if (result.Warning is not null)
            return $"Warning: {result.Warning}\n\n{json}";

        return $"{result.Rows.Count} row(s) in {result.ElapsedMs} ms\n{json}";
    }
}
