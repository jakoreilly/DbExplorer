using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using DbExplorer.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text;

namespace DbExplorer.Controllers;

[ApiController]
[Route("api/data")]
[Authorize]
[Produces("application/json")]
public sealed class DataController(
    IDataBrowsingService dataBrowsing,
    IAuditLogger audit,
    IOptions<DataBrowsingOptions> options,
    ILogger<DataController> logger) : ControllerBase
{
    private string Username => User.Identity?.Name ?? "anonymous";
    [HttpGet("page")]
    [ProducesResponseType<PagedResult<DataRow>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPage(
        [FromQuery] string schema,
        [FromQuery] string objectName,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] int sortDir = (int)SortDirection.Descending,
        [FromQuery] string? sortCol = null,
        [FromQuery] int sortColDir = (int)SortDirection.Ascending,
        [FromQuery] string? filters = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(objectName))
            return Problem("schema and objectName are required.", statusCode: 400);

        if (!TryParseFilters(filters, out var parsedFilters, out var filterError))
            return Problem(filterError, statusCode: 400);

        try
        {
            var direction = sortDir == (int)SortDirection.Ascending
                ? SortDirection.Ascending
                : SortDirection.Descending;

            var result = await dataBrowsing.GetPagedDataAsync(
                schema, objectName,
                new PagingOptions
                {
                    PageNumber = page,
                    PageSize = pageSize,
                    OrderByPrimaryKey = direction,
                    SortColumn = string.IsNullOrWhiteSpace(sortCol) ? null : sortCol,
                    SortColumnDirection = sortColDir == (int)SortDirection.Descending
                        ? SortDirection.Descending
                        : SortDirection.Ascending,
                    Filters = parsedFilters
                },
                ct);

            audit.Log(new AuditEvent(DateTimeOffset.UtcNow, Username, AuditAction.DataAccess,
                schema, objectName, result.Items.Count, -1));
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("GetPage rejected invalid identifier: {Message}", ex.Message);
            return Problem(ex.Message, statusCode: 400);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("GetPage: object not found — {Message}", ex.Message);
            return Problem(ex.Message, statusCode: 404);
        }
    }

    /// <summary>
    /// Exports data as CSV.
    /// scope=page exports only the requested page.
    /// scope=all exports up to configured MaxExportRows.
    /// </summary>
    [HttpGet("export-csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportCsv(
        [FromQuery] string schema,
        [FromQuery] string objectName,
        [FromQuery] string scope = "page",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 500,
        [FromQuery] int sortDir = (int)SortDirection.Descending,
        [FromQuery] string? sortCol = null,
        [FromQuery] int sortColDir = (int)SortDirection.Ascending,
        [FromQuery] string? filters = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(objectName))
            return Problem("schema and objectName are required.", statusCode: 400);

        if (!TryParseFilters(filters, out var parsedFilters, out var filterError))
            return Problem(filterError, statusCode: 400);

        try
        {
            var direction = sortDir == (int)SortDirection.Ascending
                ? SortDirection.Ascending
                : SortDirection.Descending;

            var normalizedScope = scope?.Trim().ToLowerInvariant();
            if (normalizedScope is not "page" and not "all")
                return Problem("scope must be either 'page' or 'all'.", statusCode: 400);

            var sortColumn = string.IsNullOrWhiteSpace(sortCol) ? null : sortCol;
            var sortColumnDirection = sortColDir == (int)SortDirection.Descending
                ? SortDirection.Descending
                : SortDirection.Ascending;

            var paging = normalizedScope == "all"
                ? new PagingOptions
                {
                    PageNumber = 1,
                    PageSize = options.Value.MaxExportRows,
                    OrderByPrimaryKey = direction,
                    SortColumn = sortColumn,
                    SortColumnDirection = sortColumnDirection,
                    Filters = parsedFilters
                }
                : new PagingOptions
                {
                    PageNumber = page,
                    PageSize = pageSize,
                    OrderByPrimaryKey = direction,
                    SortColumn = sortColumn,
                    SortColumnDirection = sortColumnDirection,
                    Filters = parsedFilters
                };

            var result = await dataBrowsing.GetPagedDataAsync(
                schema, objectName,
                paging,
                ct);

            var csv = BuildCsv(result.Items);
            var fileName = normalizedScope == "all"
                ? $"{schema}_{objectName}_all.csv"
                : $"{schema}_{objectName}_page{page}.csv";
            audit.Log(new AuditEvent(DateTimeOffset.UtcNow, Username, AuditAction.DataAccess,
                schema, objectName, result.Items.Count, -1));
            return File(Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning("ExportCsv rejected invalid identifier: {Message}", ex.Message);
            return Problem(ex.Message, statusCode: 400);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("ExportCsv: object not found — {Message}", ex.Message);
            return Problem(ex.Message, statusCode: 404);
        }
    }

    /// <summary>
    /// Parses the compact filter encoding <c>col~op~value~value2;col2~op~value</c>
    /// (each segment URL-escaped). Returns false with an error message on malformed input.
    /// </summary>
    internal static bool TryParseFilters(string? encoded, out IReadOnlyList<ColumnFilter>? filters, out string? error)
    {
        filters = null;
        error = null;
        if (string.IsNullOrWhiteSpace(encoded))
            return true;

        var list = new List<ColumnFilter>();
        foreach (var entry in encoded.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('~');
            if (parts.Length is < 2 or > 4 || string.IsNullOrWhiteSpace(parts[0]))
            {
                error = $"Malformed filter segment '{entry}'. Expected col~op~value[~value2].";
                return false;
            }

            if (!Enum.TryParse<ColumnFilterOperator>(parts[1], ignoreCase: true, out var op))
            {
                error = $"Unknown filter operator '{parts[1]}'.";
                return false;
            }

            var value = parts.Length > 2 ? Uri.UnescapeDataString(parts[2]) : "";
            var value2 = parts.Length > 3 ? Uri.UnescapeDataString(parts[3]) : null;
            list.Add(new ColumnFilter(Uri.UnescapeDataString(parts[0]), value, op, value2));
        }

        filters = list;
        return true;
    }

    /// <summary>Inverse of <see cref="TryParseFilters"/> — used by the grid to build export URLs.</summary>
    internal static string EncodeFilters(IEnumerable<ColumnFilter> filters)
        => string.Join(";", filters.Select(f =>
        {
            static string Escape(string s) => Uri.EscapeDataString(s).Replace("~", "%7E");
            var s = $"{Escape(f.ColumnName)}~{f.Operator}~{Escape(f.Value)}";
            return f.Value2 is null ? s : s + "~" + Escape(f.Value2);
        }));

    private static string BuildCsv(IReadOnlyList<DataRow> rows)
    {
        if (rows.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        var headers = rows[0].Fields.Keys.ToList();

        // Header row
        sb.AppendLine(string.Join(",", headers.Select(CsvEscape)));

        // Data rows
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", headers.Select(h => CsvEscape(row.Fields[h]?.ToString() ?? ""))));

        return sb.ToString();
    }

    private static string CsvEscape(string? value)
    {
        if (value is null) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
