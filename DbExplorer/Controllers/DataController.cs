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
    IOptions<DataBrowsingOptions> options,
    ILogger<DataController> logger) : ControllerBase
{
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
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(objectName))
            return Problem("schema and objectName are required.", statusCode: 400);

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
                    OrderByPrimaryKey = direction
                },
                ct);

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
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(objectName))
            return Problem("schema and objectName are required.", statusCode: 400);

        try
        {
            var direction = sortDir == (int)SortDirection.Ascending
                ? SortDirection.Ascending
                : SortDirection.Descending;

            var normalizedScope = scope?.Trim().ToLowerInvariant();
            if (normalizedScope is not "page" and not "all")
                return Problem("scope must be either 'page' or 'all'.", statusCode: 400);

            var paging = normalizedScope == "all"
                ? new PagingOptions
                {
                    PageNumber = 1,
                    PageSize = options.Value.MaxExportRows,
                    OrderByPrimaryKey = direction
                }
                : new PagingOptions
                {
                    PageNumber = page,
                    PageSize = pageSize,
                    OrderByPrimaryKey = direction
                };

            var result = await dataBrowsing.GetPagedDataAsync(
                schema, objectName,
                paging,
                ct);

            var csv = BuildCsv(result.Items);
            var fileName = normalizedScope == "all"
                ? $"{schema}_{objectName}_all.csv"
                : $"{schema}_{objectName}_page{page}.csv";
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
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
