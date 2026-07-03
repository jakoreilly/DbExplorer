using Dapper;
using DbExplorer.Core.Models;

namespace DbExplorer.Services;

/// <summary>
/// Compiles a single <see cref="ColumnFilter"/> into a SQL predicate.
/// The column identifier must already be catalog-validated and dialect-quoted by the caller;
/// filter values are only ever registered as parameters, never interpolated.
/// LIKE is emitted verbatim for all providers (note: PostgreSQL LIKE is case-sensitive).
/// Comparison operators pass the raw string parameter and rely on the engine's implicit
/// conversion for numeric/date columns.
/// </summary>
public static class FilterSql
{
    /// <summary>
    /// Returns the predicate clause and registers its parameters in <paramref name="args"/>,
    /// or null when the filter cannot be compiled (e.g. Between without Value2).
    /// </summary>
    public static string? BuildPredicate(string quotedColumn, string paramName, ColumnFilter filter, DynamicParameters args)
    {
        string Add(string clause, string value)
        {
            args.Add(paramName, value);
            return clause;
        }

        return filter.Operator switch
        {
            ColumnFilterOperator.IsNull => $"{quotedColumn} IS NULL",
            ColumnFilterOperator.IsNotNull => $"{quotedColumn} IS NOT NULL",
            ColumnFilterOperator.Equals => Add($"{quotedColumn} = @{paramName}", filter.Value),
            ColumnFilterOperator.NotEquals => Add($"{quotedColumn} <> @{paramName}", filter.Value),
            ColumnFilterOperator.GreaterThan => Add($"{quotedColumn} > @{paramName}", filter.Value),
            ColumnFilterOperator.LessThan => Add($"{quotedColumn} < @{paramName}", filter.Value),
            ColumnFilterOperator.StartsWith => Add($"{quotedColumn} LIKE @{paramName}", filter.Value + "%"),
            ColumnFilterOperator.Between => BuildBetween(quotedColumn, paramName, filter, args),
            _ => Add($"{quotedColumn} LIKE @{paramName}", $"%{filter.Value}%"),
        };
    }

    private static string? BuildBetween(string quotedColumn, string paramName, ColumnFilter filter, DynamicParameters args)
    {
        if (string.IsNullOrEmpty(filter.Value2))
            return null;

        args.Add(paramName, filter.Value);
        args.Add(paramName + "b", filter.Value2);
        return $"{quotedColumn} BETWEEN @{paramName} AND @{paramName}b";
    }
}
