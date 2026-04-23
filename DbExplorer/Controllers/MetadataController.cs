using DbExplorer.Core.Interfaces;
using DbExplorer.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbExplorer.Controllers;

[ApiController]
[Route("api/metadata")]
[Authorize]
[Produces("application/json")]
public sealed class MetadataController(
    IMetadataService metadata,
    ILogger<MetadataController> logger) : ControllerBase
{
    [HttpGet("schemas")]
    [ProducesResponseType<IReadOnlyList<SchemaInfo>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSchemas(CancellationToken ct)
    {
        var schemas = await metadata.GetSchemasAsync(ct);
        return Ok(schemas);
    }

    [HttpGet("objects")]
    [ProducesResponseType<IReadOnlyList<DatabaseObjectInfo>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetObjects([FromQuery] string? schema, CancellationToken ct)
    {
        var objects = await metadata.GetObjectsAsync(schema, ct);
        return Ok(objects);
    }

    [HttpGet("columns")]
    [ProducesResponseType<IReadOnlyList<ColumnInfo>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetColumns(
        [FromQuery] string schema,
        [FromQuery] string objectName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(objectName))
            return Problem("schema and objectName are required.", statusCode: 400);

        try
        {
            var cols = await metadata.GetColumnsAsync(schema, objectName, ct);
            return Ok(cols);
        }
        catch (ArgumentException ex)
        {
            return Problem(ex.Message, statusCode: 400);
        }
    }

    [HttpGet("indexes")]
    [ProducesResponseType<IReadOnlyList<IndexInfo>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetIndexes(
        [FromQuery] string schema,
        [FromQuery] string tableName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(tableName))
            return Problem("schema and tableName are required.", statusCode: 400);

        try
        {
            var indexes = await metadata.GetIndexesAsync(schema, tableName, ct);
            return Ok(indexes);
        }
        catch (ArgumentException ex)
        {
            return Problem(ex.Message, statusCode: 400);
        }
    }

    [HttpGet("foreignkeys")]
    [ProducesResponseType<IReadOnlyList<ForeignKeyInfo>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetForeignKeys(
        [FromQuery] string schema,
        [FromQuery] string tableName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(tableName))
            return Problem("schema and tableName are required.", statusCode: 400);

        try
        {
            var fks = await metadata.GetForeignKeysAsync(schema, tableName, ct);
            return Ok(fks);
        }
        catch (ArgumentException ex)
        {
            return Problem(ex.Message, statusCode: 400);
        }
    }

    [HttpGet("definition")]
    [ProducesResponseType<ObjectDefinition>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetDefinition(
        [FromQuery] string schema,
        [FromQuery] string objectName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(objectName))
            return Problem("schema and objectName are required.", statusCode: 400);

        try
        {
            var def = await metadata.GetObjectDefinitionAsync(schema, objectName, ct);
            if (def is null)
                return NotFound(new { message = $"No definition found for {schema}.{objectName}" });
            return Ok(def);
        }
        catch (ArgumentException ex)
        {
            return Problem(ex.Message, statusCode: 400);
        }
    }
}
