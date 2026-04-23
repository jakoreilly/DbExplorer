namespace DbExplorer.Core.Models;

public record SchemaInfo(string SchemaName);

public record DatabaseObjectInfo(
    string SchemaName,
    string ObjectName,
    string ObjectType   // TABLE, VIEW, PROCEDURE, SCALAR_FUNCTION, TABLE_FUNCTION
);

public record ColumnInfo(
    string SchemaName,
    string TableName,
    string ColumnName,
    int OrdinalPosition,
    bool IsNullable,
    string DataType,
    int? MaxLength,
    int? NumericPrecision,
    int? NumericScale,
    bool IsPrimaryKey,
    string? DefaultValue
);

public record IndexInfo(
    string SchemaName,
    string TableName,
    string IndexName,
    bool IsUnique,
    bool IsPrimaryKey,
    bool IsClustered,
    string Columns
);

public record ForeignKeyInfo(
    string ConstraintName,
    string SchemaName,
    string TableName,
    string ColumnName,
    string ReferencedSchema,
    string ReferencedTable,
    string ReferencedColumn
);

public record ObjectDefinition(
    string SchemaName,
    string ObjectName,
    string ObjectType,
    string? Definition
);

public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount
);

public record DataRow(IDictionary<string, object?> Fields);

public record PagingOptions
{
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}
