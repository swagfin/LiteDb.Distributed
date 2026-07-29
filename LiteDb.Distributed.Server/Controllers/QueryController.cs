using System.Text.Json;
using LiteDb.Distributed.Server.Core.Abstractions;
using LiteDb.Distributed.Server.Core.Common;
using LiteDb.Distributed.Server.Core.Exceptions;
using LiteDb.Distributed.Server.Core.Filters;
using LiteDb.Distributed.Server.Core.Models;
using LiteDb.Distributed.Server.Core.Queries;
using LiteDb.Distributed.Server.Infrastructure.Helpers;
using LiteDb.Distributed.Server.Infrastructure.Queries;
using LiteDb.Distributed.Server.Infrastructure.Replication;
using LiteDB;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [RequireClientDatabaseAuth]
    [Route("api/query")]
    public class QueryController : ControllerBase
    {
        private readonly ILocalDocumentReader _reader;
        private readonly ILocalDocumentWriter _writer;
        private readonly IReplicationSignalPublisher _replicationSignalPublisher;
        private readonly ILogger<QueryController> _logger;

        public QueryController(ILocalDocumentReader reader, ILocalDocumentWriter writer, IReplicationSignalPublisher replicationSignalPublisher, ILogger<QueryController> logger)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _replicationSignalPublisher = replicationSignalPublisher ?? throw new ArgumentNullException(nameof(replicationSignalPublisher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpPost]
        public async Task<IActionResult> ExecuteAsync([FromBody] QueryRequest request, [FromQuery] bool includeReservedFields = false, CancellationToken cancellationToken = default)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Query))
            {
                return BadRequest(new { Error = "Query is required." });
            }

            if (!SafeQueryParser.TryNormalizeSingleStatementQuery(request.Query, out string normalizedQuery, out string validationError))
            {
                return BadRequest(new { Error = validationError });
            }

            if (!SafeQueryParser.TryGetCommand(normalizedQuery, out QueryCommand command))
            {
                return BadRequest(new { Error = "Unable to determine query command." });
            }

            return command switch
            {
                QueryCommand.Select => await ExecuteSelectAsync(normalizedQuery, request.Take, includeReservedFields, cancellationToken).ConfigureAwait(false),
                QueryCommand.Insert => await ExecuteInsertAsync(normalizedQuery, cancellationToken).ConfigureAwait(false),
                QueryCommand.Update => await ExecuteUpdateAsync(normalizedQuery, request.Take, cancellationToken).ConfigureAwait(false),
                QueryCommand.Delete => await ExecuteDeleteAsync(normalizedQuery, request.Take, cancellationToken).ConfigureAwait(false),
                _ => BadRequest(new { Error = "Only SELECT, INSERT, UPDATE, DELETE are supported in safe query mode." })
            };
        }

        private async Task<IActionResult> ExecuteSelectAsync(string query, int take, bool includeReservedFields, CancellationToken cancellationToken)
        {
            string normalizedSelectQuery = LiteDbQueryRewriter.NormalizeSelectTopSyntax(query, out int? declaredTake);
            declaredTake ??= LiteDbQueryRewriter.TryReadLimitTake(normalizedSelectQuery, out int limitTake) ? limitTake : null;

            int requestedTake = take <= 0 ? 100 : Math.Clamp(take, 1, 10_000);
            int safeTake = declaredTake.HasValue ? Math.Clamp(Math.Max(requestedTake, declaredTake.Value), 1, 10_000) : requestedTake;

            try
            {
                string executableQuery = LiteDbQueryRewriter.AddLiveDocumentPredicateToSelect(normalizedSelectQuery);
                IReadOnlyList<Dictionary<string, object?>> rows = await _reader.ExecuteQueryAsync<Dictionary<string, object?>>(executableQuery, safeTake, cancellationToken).ConfigureAwait(false);
                List<Dictionary<string, object?>> visibleRows = LiteDbQueryRewriter.ShouldFilterTombstonesForSelect(query) ? rows.Where(x => !QueryRowHelpers.IsDeletedOrTombstoneRow(x)).ToList() : rows.ToList();
                IReadOnlyList<Dictionary<string, object?>> responseRows = includeReservedFields ? visibleRows : ReservedFieldSanitizer.SanitizeRowsIfNeeded(visibleRows);

                return Ok(new QueryResponse
                {
                    Query = query,
                    RequestedTake = safeTake,
                    MatchedCount = responseRows.Count,
                    AppliedCount = 0,
                    ReturnedRows = responseRows.Count,
                    Rows = responseRows
                });
            }
            catch (LiteException ex)
            {
                _logger.LogWarning(ex, "SELECT query execution failed.");
                return BadRequest(new { Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "SELECT query rejected.");
                return BadRequest(new { Error = ex.Message });
            }
        }

        private async Task<IActionResult> ExecuteInsertAsync(string query, CancellationToken cancellationToken)
        {
            if (!SafeQueryParser.TryParseInsert(query, out string collection, out JsonElement payload, out string error))
            {
                return BadRequest(new { Error = error });
            }

            if (IsReservedCollection(collection))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { Error = $"Collection '{collection}' is reserved. Use '/api/cache' endpoints." });
            }

            if (!QueryRowHelpers.TryExtractEntityId(payload, out string entityId))
            {
                return BadRequest(new { Error = "INSERT VALUES payload must include 'Id' (or '_id')." });
            }

            try
            {
                await _writer.EnsureCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
                WriteResult result = await _writer.UpsertAsync(collection, entityId, payload, cancellationToken: cancellationToken).ConfigureAwait(false);
                _replicationSignalPublisher.NotifyLocalChange($"query-insert:{collection}");

                Dictionary<string, object?> row = BuildMutationRow("INSERT", collection, entityId, result);

                return Ok(new QueryResponse
                {
                    Query = query,
                    RequestedTake = 1,
                    MatchedCount = 1,
                    AppliedCount = 1,
                    ReturnedRows = 1,
                    Rows = [row]
                });
            }
            catch (LiteException ex)
            {
                _logger.LogWarning(ex, "INSERT query execution failed.");
                return BadRequest(new { Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "INSERT query rejected.");
                return BadRequest(new { Error = ex.Message });
            }
        }

        private async Task<IActionResult> ExecuteUpdateAsync(string query, int take, CancellationToken cancellationToken)
        {
            if (!SafeQueryParser.TryParseUpdate(query, out string collection, out string whereClause, out JsonElement patchPayload, out string error))
            {
                return BadRequest(new { Error = error });
            }

            if (IsReservedCollection(collection))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { Error = $"Collection '{collection}' is reserved. Use '/api/cache' endpoints." });
            }

            try
            {
                int safeTake = take <= 0 ? 100 : Math.Clamp(take, 1, 10_000);
                IReadOnlyList<string> targetIds = await ResolveEntityIdsByWhereAsync(collection, whereClause, safeTake, cancellationToken).ConfigureAwait(false);
                if (targetIds.Count == 0)
                {
                    return Ok(BuildEmptyMutationResponse(query, safeTake));
                }

                List<Dictionary<string, object?>> rows = new List<Dictionary<string, object?>>(targetIds.Count);
                foreach (string entityId in targetIds)
                {
                    Dictionary<string, object?>? existing = await _reader.GetByIdAsync<Dictionary<string, object?>>(collection, entityId, cancellationToken).ConfigureAwait(false);
                    if (existing is null)
                    {
                        continue;
                    }

                    Dictionary<string, object?> mergedDocument = QueryRowHelpers.MergePatch(existing, patchPayload, entityId);
                    WriteResult result = await _writer.UpsertAsync(collection, entityId, mergedDocument, cancellationToken: cancellationToken).ConfigureAwait(false);
                    rows.Add(BuildMutationRow("UPDATE", collection, entityId, result));
                }

                if (rows.Count > 0)
                {
                    _replicationSignalPublisher.NotifyLocalChange($"query-update:{collection}");
                }

                return Ok(new QueryResponse
                {
                    Query = query,
                    RequestedTake = safeTake,
                    MatchedCount = targetIds.Count,
                    AppliedCount = rows.Count,
                    ReturnedRows = rows.Count,
                    Rows = rows
                });
            }
            catch (VersionMismatchException ex)
            {
                _logger.LogWarning(ex, "UPDATE query conflicted.");
                return Conflict(new { Error = ex.Message });
            }
            catch (LiteException ex)
            {
                _logger.LogWarning(ex, "UPDATE query execution failed.");
                return BadRequest(new { Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "UPDATE query rejected.");
                return BadRequest(new { Error = ex.Message });
            }
        }

        private async Task<IActionResult> ExecuteDeleteAsync(string query, int take, CancellationToken cancellationToken)
        {
            if (!SafeQueryParser.TryParseDelete(query, out string collection, out string whereClause, out string error))
            {
                return BadRequest(new { Error = error });
            }

            if (IsReservedCollection(collection))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { Error = $"Collection '{collection}' is reserved. Use '/api/cache' endpoints." });
            }

            try
            {
                int safeTake = take <= 0 ? 100 : Math.Clamp(take, 1, 10_000);
                IReadOnlyList<string> targetIds = await ResolveEntityIdsByWhereAsync(collection, whereClause, safeTake, cancellationToken).ConfigureAwait(false);
                if (targetIds.Count == 0)
                {
                    return Ok(BuildEmptyMutationResponse(query, safeTake));
                }

                List<Dictionary<string, object?>> rows = new List<Dictionary<string, object?>>(targetIds.Count);
                foreach (string entityId in targetIds)
                {
                    WriteResult result = await _writer.DeleteAsync(collection, entityId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    rows.Add(BuildMutationRow("DELETE", collection, entityId, result));
                }

                if (rows.Count > 0)
                {
                    _replicationSignalPublisher.NotifyLocalChange($"query-delete:{collection}");
                }

                return Ok(new QueryResponse
                {
                    Query = query,
                    RequestedTake = safeTake,
                    MatchedCount = targetIds.Count,
                    AppliedCount = rows.Count,
                    ReturnedRows = rows.Count,
                    Rows = rows
                });
            }
            catch (VersionMismatchException ex)
            {
                _logger.LogWarning(ex, "DELETE query conflicted.");
                return Conflict(new { Error = ex.Message });
            }
            catch (LiteException ex)
            {
                _logger.LogWarning(ex, "DELETE query execution failed.");
                return BadRequest(new { Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "DELETE query rejected.");
                return BadRequest(new { Error = ex.Message });
            }
        }

        private async Task<IReadOnlyList<string>> ResolveEntityIdsByWhereAsync(string collection, string whereClause, int take, CancellationToken cancellationToken)
        {
            // Resolve target ids via SELECT first, then apply writes through writer APIs for replication safety.
            string normalizedWhereClause = LiteDbQueryRewriter.NormalizeWhereClause(whereClause);
            string lookupQuery = string.IsNullOrWhiteSpace(whereClause)
                ? $"SELECT $ FROM {collection} WHERE {LiteDbQueryRewriter.LiveDocumentPredicate} LIMIT {take}"
                : $"SELECT $ FROM {collection} WHERE {LiteDbQueryRewriter.LiveDocumentPredicate} AND ({normalizedWhereClause}) LIMIT {take}";
            IReadOnlyList<Dictionary<string, object?>> rows = await _reader.ExecuteQueryAsync<Dictionary<string, object?>>(lookupQuery, take, cancellationToken).ConfigureAwait(false);
            List<string> ids = new List<string>(rows.Count);

            foreach (Dictionary<string, object?> row in rows)
            {
                if (QueryRowHelpers.IsDeletedOrTombstoneRow(row))
                {
                    continue;
                }

                if (!QueryRowHelpers.TryExtractEntityId(row, out string entityId))
                {
                    continue;
                }

                if (ids.Contains(entityId, StringComparer.Ordinal))
                {
                    continue;
                }

                ids.Add(entityId);
            }

            if (ids.Count == 0 && !string.IsNullOrWhiteSpace(normalizedWhereClause))
            {
                List<string> idPredicates = QueryRowHelpers.ExtractIdEqualityPredicates(normalizedWhereClause);
                foreach (string candidateId in idPredicates)
                {
                    Dictionary<string, object?>? entity = await _reader.GetByIdAsync<Dictionary<string, object?>>(collection, candidateId, cancellationToken).ConfigureAwait(false);
                    if (entity is null || ids.Contains(candidateId, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    ids.Add(candidateId);
                    if (ids.Count >= take)
                    {
                        break;
                    }
                }
            }

            return ids;
        }

        private static QueryResponse BuildEmptyMutationResponse(string query, int safeTake)
        {
            return new QueryResponse
            {
                Query = query,
                RequestedTake = safeTake,
                MatchedCount = 0,
                AppliedCount = 0,
                ReturnedRows = 0,
                Rows = []
            };
        }

        private static Dictionary<string, object?> BuildMutationRow(string operation, string collection, string entityId, WriteResult result)
        {
            return new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["collection"] = collection,
                ["id"] = entityId,
                ["version"] = result.Version,
                ["isDeleted"] = result.IsDeleted
            };
        }

        private static bool IsReservedCollection(string? collectionName)
        {
            return string.Equals(collectionName, Common.CacheCollectionName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
