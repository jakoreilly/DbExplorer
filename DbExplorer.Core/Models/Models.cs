namespace DbExplorer.Core.Models;

public enum SortDirection
{
    Ascending = 0,
    Descending = 1
}

public record SchemaInfo(string SchemaName);

public record CatalogInfo(string CatalogName);

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

public record TriggerInfo(
    string SchemaName,
    string TableName,
    string TriggerName,
    bool IsEnabled,
    string Events
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
    public SortDirection OrderByPrimaryKey { get; init; } = SortDirection.Descending;
    /// <summary>
    /// When set, the grid sorts by this column instead of the primary key.
    /// Validated and quoted by the service before use in SQL.
    /// </summary>
    public string? SortColumn { get; init; }
    public SortDirection SortColumnDirection { get; init; } = SortDirection.Ascending;
}
