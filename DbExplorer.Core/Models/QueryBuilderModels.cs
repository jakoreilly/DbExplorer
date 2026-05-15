namespace DbExplorer.Core.Models;

// ── Query Builder models ──────────────────────────────────────────────────────

public enum JoinType
{
    Inner,
    Left,
    Right,
    Full
}

public enum FilterOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Like,
    NotLike,
    IsNull,
    IsNotNull
}

/// <summary>
/// One table participating in the query (the first is the FROM table; subsequent ones are joined).
/// </summary>
public record QueryTableNode(
    /// <summary>Unique alias within this query, e.g. "t0", "t1".</summary>
    string Id,
    string SchemaName,
    string TableName,
    /// <summary>Columns to include in SELECT. Empty list means SELECT *.</summary>
    IReadOnlyList<string> SelectedColumns
);

/// <summary>
/// Describes a JOIN between two table nodes.
/// </summary>
public record QueryJoinEdge(
    JoinType JoinType,
    /// <summary>Node Id of the FK-holding table.</summary>
    string SourceNodeId,
    string SourceColumn,
    /// <summary>Node Id of the PK-holding (referenced) table.</summary>
    string TargetNodeId,
    string TargetColumn
);

/// <summary>
/// A single WHERE clause row.
/// </summary>
public record QueryFilterNode(
    /// <summary>Node Id of the table this column belongs to.</summary>
    string NodeId,
    string Column,
    FilterOperator Operator,
    /// <summary>Null for IS NULL / IS NOT NULL operators.</summary>
    string? Value
);

/// <summary>
/// Complete descriptor for a SELECT query.
/// </summary>
public record QueryGraph(
    IReadOnlyList<QueryTableNode> Tables,
    IReadOnlyList<QueryJoinEdge> Joins,
    IReadOnlyList<QueryFilterNode> Filters,
    /// <summary>Row cap applied as LIMIT / TOP. Null means no limit.</summary>
    int? Limit
);
