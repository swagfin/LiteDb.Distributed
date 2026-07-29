using LiteDb.Distributed.Server.Core.Abstractions;
using LiteDb.Distributed.Server.Core.Common;
using LiteDb.Distributed.Server.Core.Exceptions;
using LiteDb.Distributed.Server.Core.Models;
using LiteDb.Distributed.Server.Data;
using LiteDb.Distributed.Server.Infrastructure.Replication;
using LiteDb.Distributed.Server.Core.Filters;
using LiteDb.Distributed.Server.Infrastructure.Helpers;
using LiteDb.Distributed.Server.Infrastructure.Documents;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text.Json;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [RequireClientDatabaseAuth]
    [Route("api/documents/{documentName}")]
    public class DocumentsController : ControllerBase
    {
        private readonly ILocalDocumentWriter _writer;
        private readonly ILocalDocumentReader _reader;
        private readonly ILogicalDatabaseStoreProvider _logicalDatabaseStoreProvider;
        private readonly IReplicationSignalPublisher _replicationSignalPublisher;
        private readonly ILogger<DocumentsController> _logger;

        public DocumentsController(ILocalDocumentWriter writer, ILocalDocumentReader reader, ILogicalDatabaseStoreProvider logicalDatabaseStoreProvider, IReplicationSignalPublisher replicationSignalPublisher, ILogger<DocumentsController> logger)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _logicalDatabaseStoreProvider = logicalDatabaseStoreProvider ?? throw new ArgumentNullException(nameof(logicalDatabaseStoreProvider));
            _replicationSignalPublisher = replicationSignalPublisher ?? throw new ArgumentNullException(nameof(replicationSignalPublisher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpGet("/api/documents")]
        public async Task<IActionResult> GetCollectionsAsync([FromQuery] bool includeSystemCollections = false, CancellationToken cancellationToken = default)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            LiteDbNodeStore store = await _logicalDatabaseStoreProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> collections = await store.GetBusinessCollectionNamesAsync(cancellationToken).ConfigureAwait(false);
            IEnumerable<string> discoveredCollections = collections.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim());

            if (!includeSystemCollections)
            {
                discoveredCollections = discoveredCollections.Where(x => !DocumentPayloadNormalizer.IsReservedCollection(x));
            }

            List<string> responseCollections = discoveredCollections.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            stopwatch.Stop();
            _logger.LogDebug("Collection list completed. Count={Count} IncludeSystemCollections={IncludeSystemCollections} DurationMs={DurationMs}", responseCollections.Count, includeSystemCollections, stopwatch.Elapsed.TotalMilliseconds);

            return Ok(responseCollections);
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
            IReadOnlyList<Dictionary<string, object?>> responseDocuments = includeReservedFields ? documents : ReservedFieldSanitizer.SanitizeRowsIfNeeded(documents);
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

            Dictionary<string, object?> responseDocument = includeReservedFields || !ReservedFieldSanitizer.RequiresSanitization(document) ? document : ReservedFieldSanitizer.SanitizeRow(document);
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

            if (!DocumentPayloadNormalizer.TryExtractEntityId(payload, out string entityId))
            {
                stopwatch.Stop();
                _logger.LogWarning("Document post rejected due to missing Id. Collection={Collection} DurationMs={DurationMs}", documentName, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = "POST body must include an 'Id' string field." });
            }

            if (!DocumentPayloadNormalizer.TryNormalizeUpsertPayload(payload, entityId, out JsonElement normalizedPayload, out string normalizeError))
            {
                stopwatch.Stop();
                _logger.LogWarning("Document post rejected due to invalid payload. Collection={Collection} Id={Id} DurationMs={DurationMs}", documentName, entityId, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = normalizeError });
            }

            try
            {
                WriteResult result = await _writer.UpsertAsync(documentName, entityId, normalizedPayload, parentVersion, cancellationToken).ConfigureAwait(false);
                _replicationSignalPublisher.NotifyLocalChange($"document-upsert:{documentName}");
                stopwatch.Stop();

                _logger.LogDebug("Document post applied. Collection={Collection} Id={Id} Version={Version} DurationMs={DurationMs}", documentName, entityId, result.Version, stopwatch.Elapsed.TotalMilliseconds);

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

                _logger.LogDebug("Collection register completed. Collection={Collection} DurationMs={DurationMs}", documentName, stopwatch.Elapsed.TotalMilliseconds);

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

            if (!DocumentPayloadNormalizer.TryNormalizeUpsertPayload(payload, id, out JsonElement normalizedPayload, out string error))
            {
                stopwatch.Stop();
                _logger.LogWarning("Document put rejected due to invalid payload. Collection={Collection} Id={Id} DurationMs={DurationMs}", documentName, id, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = error });
            }

            try
            {
                WriteResult result = await _writer.UpsertAsync(documentName, id, normalizedPayload, parentVersion, cancellationToken).ConfigureAwait(false);
                _replicationSignalPublisher.NotifyLocalChange($"document-upsert:{documentName}");
                stopwatch.Stop();

                _logger.LogDebug("Document put applied. Collection={Collection} Id={Id} Version={Version} DurationMs={DurationMs}", documentName, id, result.Version, stopwatch.Elapsed.TotalMilliseconds);

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
                WriteResult result = await _writer.DeleteAsync(documentName, id, parentVersion, cancellationToken).ConfigureAwait(false);
                _replicationSignalPublisher.NotifyLocalChange($"document-delete:{documentName}");
                stopwatch.Stop();

                _logger.LogDebug("Document delete applied. Collection={Collection} Id={Id} Version={Version} DurationMs={DurationMs}", documentName, id, result.Version, stopwatch.Elapsed.TotalMilliseconds);

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

        private static bool TryCreateReservedCollectionRejection(string documentName, out IActionResult rejection)
        {
            if (!DocumentPayloadNormalizer.IsReservedCollection(documentName))
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
    }
}
