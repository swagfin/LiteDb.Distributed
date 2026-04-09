using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Exceptions;
using LiteDb.Distributed.Infrastructure.Replication;
using LiteDb.Distributed.Server.Filters;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text.Json;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [RequireClientDatabaseAuth]
    [Route("api/{documentName}")]
    public class DocumentsController : ControllerBase
    {
        private const string CacheCollectionName = "cache";
        private const string IdField = "Id";
        private const string InternalIdField = "_id";
        private const string SystemPrefix = "_sys_";
        private readonly ILocalDocumentWriter _writer;
        private readonly ILocalDocumentReader _reader;
        private readonly IReplicationSignalPublisher _replicationSignalPublisher;
        private readonly ILogger<DocumentsController> _logger;

        public DocumentsController(ILocalDocumentWriter writer, ILocalDocumentReader reader, IReplicationSignalPublisher replicationSignalPublisher, ILogger<DocumentsController> logger)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _replicationSignalPublisher = replicationSignalPublisher ?? throw new ArgumentNullException(nameof(replicationSignalPublisher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpGet]
        public async Task<IActionResult> ListAsync(string documentName, [FromQuery] int skip, [FromQuery] int take, [FromQuery] bool includeReservedFields = false, CancellationToken cancellationToken = default)
        {
            if (TryCreateReservedCollectionRejection(documentName, out IActionResult? reservedRejection))
            {
                return reservedRejection;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            int safeTake = take <= 0 ? 100 : Math.Clamp(take, 1, 10_000);

            _logger.LogDebug("Document list request. Collection={Collection} Skip={Skip} Take={Take}", documentName, skip, safeTake);

            IReadOnlyList<Dictionary<string, object?>> documents = await _reader.ListAsync<Dictionary<string, object?>>(documentName, skip, safeTake, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<Dictionary<string, object?>> responseDocuments = includeReservedFields ? documents : SanitizeDocumentsIfNeeded(documents);
            stopwatch.Stop();

            _logger.LogDebug("Document list completed. Collection={Collection} Count={Count} DurationMs={DurationMs}", documentName, responseDocuments.Count, stopwatch.Elapsed.TotalMilliseconds);

            return Ok(responseDocuments);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetByIdAsync(string documentName, string id, [FromQuery] bool includeReservedFields = false, CancellationToken cancellationToken = default)
        {
            if (TryCreateReservedCollectionRejection(documentName, out IActionResult? reservedRejection))
            {
                return reservedRejection;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            _logger.LogDebug("Document get request. Collection={Collection} Id={Id}", documentName, id);

            Dictionary<string, object?>? document = await _reader.GetByIdAsync<Dictionary<string, object?>>(documentName, id, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            _logger.LogDebug("Document get completed. Collection={Collection} Id={Id} Found={Found} DurationMs={DurationMs}", documentName, id, document is not null, stopwatch.Elapsed.TotalMilliseconds);

            if (document is null)
            {
                return NotFound();
            }

            Dictionary<string, object?> responseDocument = includeReservedFields || !RequiresSanitization(document) ? document : SanitizeDocument(document);
            return Ok(responseDocument);
        }

        [HttpPost]
        public async Task<IActionResult> PostAsync(string documentName, [FromBody] JsonElement payload, [FromQuery] string? parentVersion, CancellationToken cancellationToken)
        {
            if (TryCreateReservedCollectionRejection(documentName, out IActionResult? reservedRejection))
            {
                return reservedRejection;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();

            if (!TryExtractEntityId(payload, out string? entityId))
            {
                stopwatch.Stop();
                _logger.LogWarning("Document post rejected due to missing Id. Collection={Collection} DurationMs={DurationMs}", documentName, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = "POST body must include an 'Id' string field." });
            }

            if (!TryNormalizeUpsertPayload(payload, entityId, out JsonElement normalizedPayload, out string? normalizeError))
            {
                stopwatch.Stop();
                _logger.LogWarning("Document post rejected due to invalid payload. Collection={Collection} Id={Id} DurationMs={DurationMs}", documentName, entityId, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = normalizeError });
            }

            try
            {
                Core.Models.WriteResult result = await _writer.UpsertAsync(documentName, entityId, normalizedPayload, parentVersion, cancellationToken).ConfigureAwait(false);
                _replicationSignalPublisher.NotifyLocalChange($"document-upsert:{documentName}");
                stopwatch.Stop();

                _logger.LogInformation("Document post applied. Collection={Collection} Id={Id} Version={Version} DurationMs={DurationMs}", documentName, entityId, result.Version, stopwatch.Elapsed.TotalMilliseconds);

                return Ok(result);
            }
            catch (VersionMismatchException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Document post conflict. Collection={Collection} Id={Id} DurationMs={DurationMs}", documentName, entityId, stopwatch.Elapsed.TotalMilliseconds);
                return Conflict(new { Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Document post rejected. Collection={Collection} Id={Id} DurationMs={DurationMs}", documentName, entityId, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = ex.Message });
            }
        }

        [HttpPost("register")]
        public async Task<IActionResult> RegisterCollectionAsync(string documentName, CancellationToken cancellationToken)
        {
            if (TryCreateReservedCollectionRejection(documentName, out IActionResult? reservedRejection))
            {
                return reservedRejection;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            _logger.LogDebug("Collection register request. Collection={Collection}", documentName);

            try
            {
                await _writer.EnsureCollectionAsync(documentName, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                _logger.LogInformation("Collection register completed. Collection={Collection} DurationMs={DurationMs}", documentName, stopwatch.Elapsed.TotalMilliseconds);

                return Ok();
            }
            catch (ArgumentException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Collection register rejected. Collection={Collection} DurationMs={DurationMs}", documentName, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = ex.Message });
            }
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> PutAsync(string documentName, string id, [FromBody] JsonElement payload, [FromQuery] string? parentVersion, CancellationToken cancellationToken)
        {
            if (TryCreateReservedCollectionRejection(documentName, out IActionResult? reservedRejection))
            {
                return reservedRejection;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            _logger.LogDebug("Document put request. Collection={Collection} Id={Id}", documentName, id);

            if (!TryNormalizeUpsertPayload(payload, id, out JsonElement normalizedPayload, out string? error))
            {
                stopwatch.Stop();
                _logger.LogWarning("Document put rejected due to invalid payload. Collection={Collection} Id={Id} DurationMs={DurationMs}", documentName, id, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = error });
            }

            try
            {
                Core.Models.WriteResult result = await _writer.UpsertAsync(documentName, id, normalizedPayload, parentVersion, cancellationToken).ConfigureAwait(false);
                _replicationSignalPublisher.NotifyLocalChange($"document-upsert:{documentName}");
                stopwatch.Stop();

                _logger.LogInformation("Document put applied. Collection={Collection} Id={Id} Version={Version} DurationMs={DurationMs}", documentName, id, result.Version, stopwatch.Elapsed.TotalMilliseconds);

                return Ok(result);
            }
            catch (VersionMismatchException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Document put conflict. Collection={Collection} Id={Id} DurationMs={DurationMs}", documentName, id, stopwatch.Elapsed.TotalMilliseconds);
                return Conflict(new { Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Document put rejected. Collection={Collection} Id={Id} DurationMs={DurationMs}", documentName, id, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteAsync(string documentName, string id, [FromQuery] string? parentVersion, CancellationToken cancellationToken)
        {
            if (TryCreateReservedCollectionRejection(documentName, out IActionResult? reservedRejection))
            {
                return reservedRejection;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            _logger.LogDebug("Document delete request. Collection={Collection} Id={Id}", documentName, id);

            try
            {
                Core.Models.WriteResult result = await _writer.DeleteAsync(documentName, id, parentVersion, cancellationToken).ConfigureAwait(false);
                _replicationSignalPublisher.NotifyLocalChange($"document-delete:{documentName}");
                stopwatch.Stop();

                _logger.LogInformation("Document delete applied. Collection={Collection} Id={Id} Version={Version} DurationMs={DurationMs}", documentName, id, result.Version, stopwatch.Elapsed.TotalMilliseconds);

                return Ok(result);
            }
            catch (VersionMismatchException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Document delete conflict. Collection={Collection} Id={Id} DurationMs={DurationMs}", documentName, id, stopwatch.Elapsed.TotalMilliseconds);
                return Conflict(new { Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Document delete rejected. Collection={Collection} Id={Id} DurationMs={DurationMs}", documentName, id, stopwatch.Elapsed.TotalMilliseconds);
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

            return TryReadPropertyAsString(payload, "Id", out entityId);
        }

        private static bool TryReadPropertyAsString(JsonElement payload, string propertyName, out string value)
        {
            value = string.Empty;

            if (!payload.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? candidate = property.GetString();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            value = candidate;
            return true;
        }

        private static bool TryNormalizeUpsertPayload(JsonElement payload, string routeId, out JsonElement normalizedPayload, out string error)
        {
            normalizedPayload = default;
            error = string.Empty;

            if (payload.ValueKind != JsonValueKind.Object)
            {
                error = "PUT body must be a JSON object.";
                return false;
            }

            if (CanUsePayloadAsIs(payload, routeId))
            {
                normalizedPayload = payload;
                return true;
            }

            using MemoryStream stream = new MemoryStream();
            using Utf8JsonWriter writer = new Utf8JsonWriter(stream);

            // Route id is source-of-truth for upsert identity; body Id/_id is overwritten.
            writer.WriteStartObject();
            foreach (JsonProperty property in payload.EnumerateObject())
            {
                if (IsReservedPayloadField(property.Name))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteString("Id", routeId);
            writer.WriteEndObject();
            writer.Flush();

            stream.Position = 0;
            using JsonDocument document = JsonDocument.Parse(stream);
            normalizedPayload = document.RootElement.Clone();
            return true;
        }

        private static bool CanUsePayloadAsIs(JsonElement payload, string routeId)
        {
            if (payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            bool hasId = false;

            foreach (JsonProperty property in payload.EnumerateObject())
            {
                if (string.Equals(property.Name, InternalIdField, StringComparison.OrdinalIgnoreCase) || property.Name.StartsWith(SystemPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!string.Equals(property.Name, IdField, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                string? id = property.Value.GetString();
                if (!string.Equals(id, routeId, StringComparison.Ordinal))
                {
                    return false;
                }

                hasId = true;
            }

            return hasId;
        }

        private static bool IsReservedPayloadField(string propertyName)
        {
            return string.Equals(propertyName, IdField, StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, InternalIdField, StringComparison.OrdinalIgnoreCase)
                || propertyName.StartsWith(SystemPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryCreateReservedCollectionRejection(string documentName, out IActionResult rejection)
        {
            if (!IsReservedCollection(documentName))
            {
                rejection = null!;
                return false;
            }

            rejection = new ObjectResult(new { Error = $"Collection '{documentName}' is reserved. Use '/api/cache' endpoints." })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return true;
        }

        private static bool IsReservedCollection(string? collectionName)
        {
            return string.Equals(collectionName, CacheCollectionName, StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<Dictionary<string, object?>> SanitizeDocumentsIfNeeded(IReadOnlyList<Dictionary<string, object?>> documents)
        {
            List<Dictionary<string, object?>>? sanitized = null;

            for (int i = 0; i < documents.Count; i++)
            {
                Dictionary<string, object?> document = documents[i];
                if (!RequiresSanitization(document))
                {
                    sanitized?.Add(document);
                    continue;
                }

                if (sanitized is null)
                {
                    sanitized = new List<Dictionary<string, object?>>(documents.Count);
                    for (int j = 0; j < i; j++)
                    {
                        sanitized.Add(documents[j]);
                    }
                }

                sanitized.Add(SanitizeDocument(document));
            }

            return sanitized ?? documents;
        }

        private static bool RequiresSanitization(IReadOnlyDictionary<string, object?> source)
        {
            foreach (KeyValuePair<string, object?> entry in source)
            {
                if (string.Equals(entry.Key, InternalIdField, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (entry.Key.StartsWith(SystemPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static Dictionary<string, object?> SanitizeDocument(IReadOnlyDictionary<string, object?> source)
        {
            Dictionary<string, object?> sanitized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            object? internalIdValue = null;

            foreach (KeyValuePair<string, object?> entry in source)
            {
                if (string.Equals(entry.Key, InternalIdField, StringComparison.OrdinalIgnoreCase))
                {
                    internalIdValue = entry.Value;
                    continue;
                }

                if (entry.Key.StartsWith(SystemPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sanitized[entry.Key] = entry.Value;
            }

            if (!sanitized.ContainsKey(IdField) && internalIdValue is not null)
            {
                sanitized[IdField] = internalIdValue;
            }

            return sanitized;
        }
    }
}
