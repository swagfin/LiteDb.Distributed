using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Exceptions;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text.Json;

namespace LiteDb.Distributed.Server.Controllers;

[ApiController]
[Route("api/{documentName}")]
public sealed class DocumentsController : ControllerBase
{
    private readonly ILocalDocumentWriter _writer;
    private readonly ILocalDocumentReader _reader;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        ILocalDocumentWriter writer,
        ILocalDocumentReader reader,
        ILogger<DocumentsController> logger)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet]
    public async Task<IActionResult> ListAsync(string documentName, [FromQuery] int skip, [FromQuery] int take, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var safeTake = take <= 0 ? 100 : take;

        _logger.LogDebug(
            "Document list request. Collection={Collection} Skip={Skip} Take={Take}",
            documentName,
            skip,
            safeTake);

        var documents = await _reader.ListAsync<Dictionary<string, object?>>(documentName, skip, safeTake, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        _logger.LogDebug(
            "Document list completed. Collection={Collection} Count={Count} DurationMs={DurationMs}",
            documentName,
            documents.Count,
            stopwatch.Elapsed.TotalMilliseconds);

        return Ok(documents);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetByIdAsync(string documentName, string id, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogDebug("Document get request. Collection={Collection} Id={Id}", documentName, id);

        var document = await _reader.GetByIdAsync<Dictionary<string, object?>>(documentName, id, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        _logger.LogDebug(
            "Document get completed. Collection={Collection} Id={Id} Found={Found} DurationMs={DurationMs}",
            documentName,
            id,
            document is not null,
            stopwatch.Elapsed.TotalMilliseconds);

        return document is null ? NotFound() : Ok(document);
    }

    [HttpPost]
    public async Task<IActionResult> PostAsync(string documentName, [FromBody] JsonElement payload, [FromQuery] string? parentVersion, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!TryExtractEntityId(payload, out var entityId))
        {
            stopwatch.Stop();
            _logger.LogWarning(
                "Document post rejected due to missing Id. Collection={Collection} DurationMs={DurationMs}",
                documentName,
                stopwatch.Elapsed.TotalMilliseconds);
            return BadRequest(new { Error = "POST body must include an 'Id' string field." });
        }

        try
        {
            var result = await _writer.UpsertAsync(documentName, entityId, payload, parentVersion, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            _logger.LogInformation(
                "Document post applied. Collection={Collection} Id={Id} Version={Version} DurationMs={DurationMs}",
                documentName,
                entityId,
                result.Version,
                stopwatch.Elapsed.TotalMilliseconds);

            return Ok(result);
        }
        catch (VersionMismatchException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                ex,
                "Document post conflict. Collection={Collection} Id={Id} DurationMs={DurationMs}",
                documentName,
                entityId,
                stopwatch.Elapsed.TotalMilliseconds);
            return Conflict(new { Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                ex,
                "Document post rejected. Collection={Collection} Id={Id} DurationMs={DurationMs}",
                documentName,
                entityId,
                stopwatch.Elapsed.TotalMilliseconds);
            return BadRequest(new { Error = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> PutAsync(string documentName, string id, [FromBody] JsonElement payload, [FromQuery] string? parentVersion, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogDebug("Document put request. Collection={Collection} Id={Id}", documentName, id);

        try
        {
            var result = await _writer.UpsertAsync(documentName, id, payload, parentVersion, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            _logger.LogInformation(
                "Document put applied. Collection={Collection} Id={Id} Version={Version} DurationMs={DurationMs}",
                documentName,
                id,
                result.Version,
                stopwatch.Elapsed.TotalMilliseconds);

            return Ok(result);
        }
        catch (VersionMismatchException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                ex,
                "Document put conflict. Collection={Collection} Id={Id} DurationMs={DurationMs}",
                documentName,
                id,
                stopwatch.Elapsed.TotalMilliseconds);
            return Conflict(new { Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                ex,
                "Document put rejected. Collection={Collection} Id={Id} DurationMs={DurationMs}",
                documentName,
                id,
                stopwatch.Elapsed.TotalMilliseconds);
            return BadRequest(new { Error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteAsync(string documentName, string id, [FromQuery] string? parentVersion, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogDebug("Document delete request. Collection={Collection} Id={Id}", documentName, id);

        try
        {
            var result = await _writer
                .DeleteAsync(documentName, id, parentVersion, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();

            _logger.LogInformation(
                "Document delete applied. Collection={Collection} Id={Id} Version={Version} DurationMs={DurationMs}",
                documentName,
                id,
                result.Version,
                stopwatch.Elapsed.TotalMilliseconds);

            return Ok(result);
        }
        catch (VersionMismatchException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                ex,
                "Document delete conflict. Collection={Collection} Id={Id} DurationMs={DurationMs}",
                documentName,
                id,
                stopwatch.Elapsed.TotalMilliseconds);
            return Conflict(new { Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                ex,
                "Document delete rejected. Collection={Collection} Id={Id} DurationMs={DurationMs}",
                documentName,
                id,
                stopwatch.Elapsed.TotalMilliseconds);
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
