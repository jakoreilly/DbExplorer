using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using SqlKata;
using SqlKata.Compilers;

namespace DbExplorer.Services;

/// <summary>
/// Compiles a <see cref="QueryGraph"/> to provider-correct SQL via SqlKata.
/// Stateless — safe to register as Scoped alongside <see cref="IDbConnectionFactory"/>.
/// </summary>
public sealed class QueryBuilderService : IQueryBuilderService
{
    private readonly IDbConnectionFactory _factory;

    public QueryBuilderService(IDbConnectionFactory factory) => _factory = factory;

    private Compiler GetCompiler() => _factory.Provider switch
    {
        DatabaseProvider.PostgreSql => new PostgresCompiler(),
        DatabaseProvider.MySql      => new MySqlCompiler(),
        _                           => new SqlServerCompiler()
    };

    public (string Sql, IReadOnlyList<object?> Bindings) Compile(QueryGraph graph)
    {
        if (graph.Tables.Count == 0)
            return ("-- Add a table to start building a query", Array.Empty<object?>());

        var baseTable = graph.Tables[0];
        // Let SqlKata quote schema and table identifiers — do not pre-quote
        var fromExpr = $"{baseTable.SchemaName}.{baseTable.TableName} AS {baseTable.Id}";
        var query = new Query(fromExpr);

        // SELECT
        var selectCols = BuildSelectColumns(graph);
        if (selectCols.Count > 0)
            query.Select(selectCols.ToArray());
        else
            query.SelectRaw("*");

        // JOINs
        foreach (var join in graph.Joins)
        {
            var target = graph.Tables.FirstOrDefault(t => t.Id == join.TargetNodeId);
            if (target is null) continue;

            // Let SqlKata quote — pass unquoted schema.table
            var targetExpr = $"{target.SchemaName}.{target.TableName} AS {join.TargetNodeId}";
            var onLeft     = $"{join.SourceNodeId}.{join.SourceColumn}";
            var onRight    = $"{join.TargetNodeId}.{join.TargetColumn}";

            query = join.JoinType switch
            {
                JoinType.Left  => query.LeftJoin(targetExpr, onLeft, onRight),
                JoinType.Right => query.RightJoin(targetExpr, onLeft, onRight),
                JoinType.Full  => query.Join(targetExpr, j => j.On(onLeft, onRight), "FULL OUTER"),
                _              => query.Join(targetExpr, onLeft, onRight)
            };
        }

        // WHERE filters — skip any filter with a missing column name
        foreach (var filter in graph.Filters)
        {
            if (!string.IsNullOrWhiteSpace(filter.Column))
                ApplyFilter(query, filter);
        }

        // LIMIT / TOP
        if (graph.Limit.HasValue)
            query.Limit(graph.Limit.Value);

        // result.ToString() inlines all bindings as SQL literals (safe read-only usage);
        // this lets IAdHocQueryService.ExecuteQueryAsync run the SQL without separate bindings.
        var result = GetCompiler().Compile(query);
        return (result.ToString(), Array.Empty<object?>());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static List<string> BuildSelectColumns(QueryGraph graph)
    {
        var list = new List<string>();
        foreach (var table in graph.Tables)
            foreach (var col in table.SelectedColumns)
                list.Add($"{table.Id}.{col}");  // SqlKata quotes each dotted segment
        return list;
    }

    private static void ApplyFilter(Query query, QueryFilterNode f)
    {
        var col = $"{f.NodeId}.{f.Column}";  // SqlKata quotes each dotted segment
        switch (f.Operator)
        {
            case FilterOperator.Equals:             query.Where(col, f.Value); break;
            case FilterOperator.NotEquals:          query.WhereNot(col, f.Value); break;
            case FilterOperator.GreaterThan:        query.Where(col, ">",  f.Value); break;
            case FilterOperator.GreaterThanOrEqual: query.Where(col, ">=", f.Value); break;
            case FilterOperator.LessThan:           query.Where(col, "<",  f.Value); break;
            case FilterOperator.LessThanOrEqual:    query.Where(col, "<=", f.Value); break;
            case FilterOperator.Like:               query.WhereLike(col, f.Value ?? ""); break;
            case FilterOperator.NotLike:            query.WhereNotLike(col, f.Value ?? ""); break;
            case FilterOperator.IsNull:             query.WhereNull(col); break;
            case FilterOperator.IsNotNull:          query.WhereNotNull(col); break;
        }
    }
}
