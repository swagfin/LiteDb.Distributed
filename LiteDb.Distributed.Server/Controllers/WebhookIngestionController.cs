using LiteDb.Distributed.Server.Core.Abstractions;
using LiteDb.Distributed.Server.Core.Context;
using LiteDb.Distributed.Server.Core.Exceptions;
using LiteDb.Distributed.Server.Core.Models;
using LiteDb.Distributed.Server.Infrastructure.Documents;
using LiteDb.Distributed.Server.Infrastructure.Replication;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text.Json;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [Route("webhook-ingestion/{databaseName}/{apiKey}")]
    public class WebhookIngestionController : ControllerBase
    {
        private readonly IDatabaseRequestContextResolver _databaseRequestContextResolver;
        private readonly IDatabaseContextAccessor _databaseContextAccessor;
        private readonly ILocalDocumentWriter _writer;
        private readonly IReplicationSignalPublisher _replicationSignalPublisher;
        private readonly ILogger<WebhookIngestionController> _logger;

        public WebhookIngestionController(IDatabaseRequestContextResolver databaseRequestContextResolver, IDatabaseContextAccessor databaseContextAccessor, ILocalDocumentWriter writer, IReplicationSignalPublisher replicationSignalPublisher, ILogger<WebhookIngestionController> logger)
        {
            _databaseRequestContextResolver = databaseRequestContextResolver ?? throw new ArgumentNullException(nameof(databaseRequestContextResolver));
            _databaseContextAccessor = databaseContextAccessor ?? throw new ArgumentNullException(nameof(databaseContextAccessor));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _replicationSignalPublisher = replicationSignalPublisher ?? throw new ArgumentNullException(nameof(replicationSignalPublisher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpGet]
        public IActionResult Get() => Ok("Ingestion endpoint is Ready!");

        [HttpPost]
        public async Task<IActionResult> IngestAsync(string databaseName, string apiKey, [FromBody] WebhookIngestionRequest request, CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            if (request is null)
            {
                return BadRequest(new { Error = "Webhook payload is required." });
            }

            if (!TryValidateRequest(request, out string collection, out string action, out string entityId, out IActionResult? validationError))
            {
                return validationError!;
            }

            try
            {
                DatabaseRequestContext databaseContext = await ResolveDatabaseContextAsync(databaseName, apiKey, cancellationToken).ConfigureAwait(false);
                using IDisposable scope = _databaseContextAccessor.BeginScope(databaseContext);

                if (IsDeleteAction(action))
                {
                    if (!databaseContext.CanDeleteDocument)
                    {
                        return StatusCode(StatusCodes.Status403Forbidden, new { Error = "ApiKey is not allowed to delete documents." });
                    }

                    WriteResult deleteResult = await _writer.DeleteAsync(collection, entityId, parentVersion: null, cancellationToken).ConfigureAwait(false);
                    _replicationSignalPublisher.NotifyLocalChange($"webhook-delete:{collection}");
                    stopwatch.Stop();

                    _logger.LogInformation("Webhook delete ingested. Database={Database} Collection={Collection} Id={Id} EventId={EventId} DurationMs={DurationMs}", databaseContext.DatabaseName, collection, entityId, request.EventId, stopwatch.Elapsed.TotalMilliseconds);

                    return Ok(BuildResponse(true, databaseContext.DatabaseName, collection, entityId, "Delete", deleteResult));
                }

                if (!databaseContext.CanWriteDocument && !databaseContext.CanUpdateDocument)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new { Error = "ApiKey is not allowed to write documents." });
                }

                if (!DocumentPayloadNormalizer.TryNormalizeUpsertPayload(request.Data, entityId, out JsonElement normalizedPayload, out string normalizeError))
                {
                    return BadRequest(new { Error = normalizeError });
                }

                WriteResult upsertResult = await _writer.UpsertAsync(collection, entityId, normalizedPayload, parentVersion: null, cancellationToken).ConfigureAwait(false);
                _replicationSignalPublisher.NotifyLocalChange($"webhook-upsert:{collection}");
                stopwatch.Stop();

                _logger.LogInformation("Webhook upsert ingested. Database={Database} Collection={Collection} Id={Id} Action={Action} EventId={EventId} DurationMs={DurationMs}", databaseContext.DatabaseName, collection, entityId, action, request.EventId, stopwatch.Elapsed.TotalMilliseconds);

                return Ok(BuildResponse(true, databaseContext.DatabaseName, collection, entityId, "Upsert", upsertResult));
            }
            catch (UnauthorizedAccessException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Webhook ingestion authorization failed. Database={Database} Collection={Collection} DurationMs={DurationMs}", databaseName, collection, stopwatch.Elapsed.TotalMilliseconds);
                return StatusCode(StatusCodes.Status403Forbidden, new { Error = ex.Message });
            }
            catch (VersionMismatchException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Webhook ingestion conflict. Database={Database} Collection={Collection} Id={Id} DurationMs={DurationMs}", databaseName, collection, entityId, stopwatch.Elapsed.TotalMilliseconds);
                return Conflict(new { Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Webhook ingestion rejected. Database={Database} Collection={Collection} Id={Id} DurationMs={DurationMs}", databaseName, collection, entityId, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = ex.Message });
            }
        }

        private async Task<DatabaseRequestContext> ResolveDatabaseContextAsync(string databaseName, string apiKey, CancellationToken cancellationToken)
        {
            HeaderDictionary headers = new HeaderDictionary
            {
                ["Database"] = databaseName,
                ["ApiKey"] = apiKey
            };

            return await _databaseRequestContextResolver.ResolveAsync(headers, cancellationToken).ConfigureAwait(false);
        }

        private bool TryValidateRequest(WebhookIngestionRequest request, out string collection, out string action, out string entityId, out IActionResult? error)
        {
            collection = (request.EntityName ?? string.Empty).Trim();
            action = (request.Action ?? string.Empty).Trim();
            entityId = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(collection))
            {
                error = BadRequest(new { Error = "Webhook payload must include entityName." });
                return false;
            }

            if (DocumentPayloadNormalizer.IsReservedCollection(collection))
            {
                error = BadRequest(new { Error = $"Collection '{collection}' is reserved." });
                return false;
            }

            if (!IsUpsertAction(action) && !IsDeleteAction(action))
            {
                error = BadRequest(new { Error = "Webhook action must be Add, Update, or Delete." });
                return false;
            }

            if (request.Data.ValueKind != JsonValueKind.Object)
            {
                error = BadRequest(new { Error = "Webhook payload must include data as a JSON object." });
                return false;
            }

            if (!TryExtractEntityId(request.Data, out entityId, out string idError))
            {
                error = BadRequest(new { Error = idError });
                return false;
            }

            return true;
        }

        private static bool TryExtractEntityId(JsonElement data, out string entityId, out string error)
        {
            entityId = string.Empty;
            error = string.Empty;

            if (TryReadPropertyAsEntityId(data, "Id", out entityId) || TryReadPropertyAsEntityId(data, "id", out entityId))
            {
                return true;
            }

            JsonProperty? firstProperty = null;
            foreach (JsonProperty property in data.EnumerateObject())
            {
                firstProperty = property;
                break;
            }

            if (firstProperty is null)
            {
                error = "Webhook data object must include at least one primary key property.";
                return false;
            }

            if (!TryConvertPrimaryKeyValue(firstProperty.Value.Value, out entityId))
            {
                error = $"Webhook data primary key property '{firstProperty.Value.Name}' must be a string, number, or boolean value.";
                return false;
            }

            return true;
        }

        private static bool TryReadPropertyAsEntityId(JsonElement data, string propertyName, out string entityId)
        {
            entityId = string.Empty;

            if (!data.TryGetProperty(propertyName, out JsonElement property))
            {
                return false;
            }

            return TryConvertPrimaryKeyValue(property, out entityId);
        }

        private static bool TryConvertPrimaryKeyValue(JsonElement value, out string entityId)
        {
            entityId = string.Empty;

            if (value.ValueKind == JsonValueKind.String)
            {
                entityId = value.GetString()?.Trim() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(entityId);
            }

            if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            {
                entityId = value.GetRawText().Trim();
                return !string.IsNullOrWhiteSpace(entityId);
            }

            return false;
        }

        private static bool IsUpsertAction(string action)
        {
            return string.Equals(action, "Add", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "Update", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDeleteAction(string action)
        {
            return string.Equals(action, "Delete", StringComparison.OrdinalIgnoreCase);
        }

        private static object BuildResponse(bool accepted, string database, string collection, string entityId, string action, WriteResult result)
        {
            return new
            {
                Accepted = accepted,
                Database = database,
                Collection = collection,
                EntityId = entityId,
                Action = action,
                Version = result.Version,
                IsDeleted = result.IsDeleted,
                CommittedUtc = result.CommittedUtc
            };
        }
    }

    public class WebhookIngestionRequest
    {
        public string? WebhookId { get; set; }
        public string? WebhookName { get; set; }
        public string? EventId { get; set; }
        public string? EntityName { get; set; }
        public string? Action { get; set; }
        public string? PrimaryKey { get; set; }
        public DateTimeOffset? OccurredDate { get; set; }
        public JsonElement Data { get; set; }
    }
}
