using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace DbExplorer.Controllers;

[ApiController]
[Route("api/data")]
[Authorize]
[Produces("application/json")]
public sealed class DataController(
    IDataBrowsingService dataBrowsing,
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
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(objectName))
            return Problem("schema and objectName are required.", statusCode: 400);

        try
        {
            var result = await dataBrowsing.GetPagedDataAsync(
                schema, objectName,
                new PagingOptions { PageNumber = page, PageSize = pageSize },
                ct);

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Problem(ex.Message, statusCode: 400);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(ex.Message, statusCode: 404);
        }
    }

    /// <summary>
    /// Exports the current page as CSV. Only exports the same capped page — not the full table.
    /// </summary>
    [HttpGet("export-csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportCsv(
        [FromQuery] string schema,
        [FromQuery] string objectName,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 500,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(objectName))
            return Problem("schema and objectName are required.", statusCode: 400);

        try
        {
            var result = await dataBrowsing.GetPagedDataAsync(
                schema, objectName,
                new PagingOptions { PageNumber = page, PageSize = pageSize },
                ct);

            var csv = BuildCsv(result.Items);
            var fileName = $"{schema}_{objectName}_page{page}.csv";
            return File(Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
        }
        catch (ArgumentException ex)
        {
            return Problem(ex.Message, statusCode: 400);
        }
        catch (InvalidOperationException ex)
        {
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
