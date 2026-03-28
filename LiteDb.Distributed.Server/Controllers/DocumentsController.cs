using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Exceptions;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace LiteDb.Distributed.Server.Controllers;

[ApiController]
[Route("api/{documentName}")]
public sealed class DocumentsController : ControllerBase
{
    private readonly ILocalDocumentWriter _writer;
    private readonly ILocalDocumentReader _reader;

    public DocumentsController(ILocalDocumentWriter writer, ILocalDocumentReader reader)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync(string documentName, [FromQuery] int skip, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var safeTake = take <= 0 ? 100 : take;
        var documents = await _reader.ListAsync<Dictionary<string, object?>>(documentName, skip, safeTake, cancellationToken).ConfigureAwait(false);

        return Ok(documents);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetByIdAsync(string documentName, string id, CancellationToken cancellationToken)
    {
        var document = await _reader.GetByIdAsync<Dictionary<string, object?>>(documentName, id, cancellationToken).ConfigureAwait(false);

        return document is null ? NotFound() : Ok(document);
    }

    [HttpPost]
    public async Task<IActionResult> PostAsync(string documentName, [FromBody] JsonElement payload, [FromQuery] string? parentVersion, CancellationToken cancellationToken)
    {
        if (!TryExtractEntityId(payload, out var entityId))
        {
            return BadRequest(new { Error = "POST body must include an 'Id' string field." });
        }

        try
        {
            var result = await _writer.UpsertAsync(documentName, entityId, payload, parentVersion, cancellationToken).ConfigureAwait(false);

            return Ok(result);
        }
        catch (VersionMismatchException ex)
        {
            return Conflict(new { Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> PutAsync(string documentName, string id, [FromBody] JsonElement payload, [FromQuery] string? parentVersion, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _writer.UpsertAsync(documentName, id, payload, parentVersion, cancellationToken).ConfigureAwait(false);

            return Ok(result);
        }
        catch (VersionMismatchException ex)
        {
            return Conflict(new { Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteAsync(string documentName, string id, [FromQuery] string? parentVersion, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _writer
                .DeleteAsync(documentName, id, parentVersion, cancellationToken)
                .ConfigureAwait(false);

            return Ok(result);
        }
        catch (VersionMismatchException ex)
        {
            return Conflict(new { Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }

    private static bool TryExtractEntityId(JsonElement payload, out string entityId)
    {
        entityId = string.Empty;

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return TryReadPropertyAsString(payload, "Id", out entityId)
               || TryReadPropertyAsString(payload, "id", out entityId);
    }

    private static bool TryReadPropertyAsString(JsonElement payload, string propertyName, out string value)
    {
        value = string.Empty;

        if (!payload.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        value = candidate;
        return true;
    }
}
