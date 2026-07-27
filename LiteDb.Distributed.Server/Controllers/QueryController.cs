using System.Text.Json;
using System.Text.RegularExpressions;
using LiteDb.Distributed.Server.Domain.Abstractions;
using LiteDb.Distributed.Server.Domain.Common;
using LiteDb.Distributed.Server.Domain.Exceptions;
using LiteDb.Distributed.Server.Domain.Models;
using LiteDb.Distributed.Server.Replication;
using LiteDb.Distributed.Server.Filters;
using LiteDb.Distributed.Server.Helpers;
using LiteDB;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [RequireClientDatabaseAuth]
    [Route("api/query")]
    public class QueryController : ControllerBase
    {
        private static readonly Regex FirstKeywordRegex = new("^(?<cmd>[a-zA-Z]+)", RegexOptions.Compiled);
        private static readonly Regex IdentifierRegex = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);
        private static readonly Regex DollarIdAliasRegex = new("(?<![A-Za-z0-9_])\\$_id(?![A-Za-z0-9_])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex IdAliasRegex = new("(?<![A-Za-z0-9_])id(?![A-Za-z0-9_])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex IdEqualityLiteralRegex = new("_id\\s*=\\s*('([^']*)'|\"([^\"]*)\"|([A-Za-z0-9_\\-]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TombstonePredicateRegex = new("(_sys_deleted|_sys_tombstone)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

            if (!TryNormalizeSingleStatementQuery(request.Query, out string? normalizedQuery, out string? validationError))
            {
                return BadRequest(new { Error = validationError });
            }

            if (!TryGetCommand(normalizedQuery, out string? command))
            {
                return BadRequest(new { Error = "Unable to determine query command." });
            }

            return command.ToLowerInvariant() switch
            {
                "select" => await ExecuteSelectAsync(normalizedQuery, request.Take, includeReservedFields, cancellationToken).ConfigureAwait(false),
                "insert" => await ExecuteInsertAsync(normalizedQuery, cancellationToken).ConfigureAwait(false),
                "update" => await ExecuteUpdateAsync(normalizedQuery, request.Take, cancellationToken).ConfigureAwait(false),
                "delete" => await ExecuteDeleteAsync(normalizedQuery, request.Take, cancellationToken).ConfigureAwait(false),
                _ => BadRequest(new { Error = "Only SELECT, INSERT, UPDATE, DELETE are supported in safe query mode." })
            };
        }

        private async Task<IActionResult> ExecuteSelectAsync(string query, int take, bool includeReservedFields, CancellationToken cancellationToken)
        {
            int safeTake = take <= 0 ? 100 : Math.Clamp(take, 1, 10_000);

            try
            {
                IReadOnlyList<Dictionary<string, object?>> rows = await _reader.ExecuteQueryAsync<Dictionary<string, object?>>(query, safeTake, cancellationToken).ConfigureAwait(false);
                List<Dictionary<string, object?>> visibleRows = ShouldFilterTombstonesForSelect(query) ? rows.Where(x => !IsDeletedOrTombstoneRow(x)).ToList() : rows.ToList();
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
            if (!TryParseInsert(query, out string? collection, out JsonElement payload, out string? error))
            {
                return BadRequest(new { Error = error });
            }

            if (IsReservedCollection(collection))
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { Error = $"Collection '{collection}' is reserved. Use '/api/cache' endpoints." });
            }

            if (!TryExtractEntityId(payload, out string? entityId))
            {
                return BadRequest(new { Error = "INSERT VALUES payload must include 'Id' (or '_id')." });
            }

            try
            {
                await _writer.EnsureCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
                WriteResult result = await _writer.UpsertAsync(collection, entityId, payload, cancellationToken: cancellationToken).ConfigureAwait(false);
                _replicationSignalPublisher.NotifyLocalChange($"query-insert:{collection}");

                Dictionary<string, object?> row = new Dictionary<string, object?>
                {
                    ["operation"] = "INSERT",
                    ["collection"] = collection,
                    ["id"] = entityId,
                    ["version"] = result.Version,
                    ["isDeleted"] = result.IsDeleted
                };

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
            if (!TryParseUpdate(query, out string? collection, out string? whereClause, out JsonElement patchPayload, out string? error))
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
                    return Ok(new QueryResponse
                    {
                        Query = query,
                        RequestedTake = safeTake,
                        MatchedCount = 0,
                        AppliedCount = 0,
                        ReturnedRows = 0,
                        Rows = []
                    });
                }

                List<Dictionary<string, object?>> rows = new List<Dictionary<string, object?>>(targetIds.Count);
                foreach (string entityId in targetIds)
                {
                    Dictionary<string, object?>? existing = await _reader.GetByIdAsync<Dictionary<string, object?>>(collection, entityId, cancellationToken).ConfigureAwait(false);
                    if (existing is null)
                    {
                        continue;
                    }

                    Dictionary<string, object?> mergedDocument = MergePatch(existing, patchPayload, entityId);
                    WriteResult result = await _writer.UpsertAsync(collection, entityId, mergedDocument, cancellationToken: cancellationToken).ConfigureAwait(false);
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["operation"] = "UPDATE",
                        ["collection"] = collection,
                        ["id"] = entityId,
                        ["version"] = result.Version,
                        ["isDeleted"] = result.IsDeleted
                    });
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
            if (!TryParseDelete(query, out string? collection, out string? whereClause, out string? error))
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
                    return Ok(new QueryResponse
                    {
                        Query = query,
                        RequestedTake = safeTake,
                        MatchedCount = 0,
                        AppliedCount = 0,
                        ReturnedRows = 0,
                        Rows = []
                    });
                }

                List<Dictionary<string, object?>> rows = new List<Dictionary<string, object?>>(targetIds.Count);
                foreach (string entityId in targetIds)
                {
                    WriteResult result = await _writer.DeleteAsync(collection, entityId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["operation"] = "DELETE",
                        ["collection"] = collection,
                        ["id"] = entityId,
                        ["version"] = result.Version,
                        ["isDeleted"] = result.IsDeleted
                    });
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

        private static bool TryNormalizeSingleStatementQuery(string rawQuery, out string normalizedQuery, out string error)
        {
            string query = (rawQuery ?? string.Empty).Trim();
            if (query.EndsWith(';'))
            {
                query = query[..^1].TrimEnd();
            }

            if (query.Length == 0)
            {
                normalizedQuery = string.Empty;
                error = "Query is required.";
                return false;
            }

            if (query.Contains(';'))
            {
                normalizedQuery = string.Empty;
                error = "Only one query statement is allowed.";
                return false;
            }

            if (!TryGetCommand(query, out string? command))
            {
                normalizedQuery = string.Empty;
                error = "Unable to determine query command.";
                return false;
            }

            if (!string.Equals(command, "select", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(command, "insert", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(command, "update", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(command, "delete", StringComparison.OrdinalIgnoreCase))
            {
                normalizedQuery = string.Empty;
                error = "Only SELECT, INSERT, UPDATE, DELETE are supported in safe query mode.";
                return false;
            }

            normalizedQuery = query;
            error = string.Empty;
            return true;
        }

        private static bool TryParseInsert(string query, out string collection, out JsonElement payload, out string error)
        {
            collection = string.Empty;
            payload = default;
            error = string.Empty;

            string remainder = query["insert".Length..].TrimStart();
            if (!TryConsumeKeyword(ref remainder, "into"))
            {
                error = "INSERT format must be: INSERT INTO <collection> VALUES <json-object>.";
                return false;
            }

            if (!TryConsumeIdentifier(ref remainder, out collection))
            {
                error = "INSERT collection name is missing or invalid.";
                return false;
            }

            if (!TryConsumeKeyword(ref remainder, "values"))
            {
                error = "INSERT format must be: INSERT INTO <collection> VALUES <json-object>.";
                return false;
            }

            if (!TryParseJsonObject(remainder, out payload, out error))
            {
                return false;
            }

            return true;
        }

        private static bool TryParseUpdate(string query, out string collection, out string whereClause, out JsonElement patchPayload, out string error)
        {
            collection = string.Empty;
            whereClause = string.Empty;
            patchPayload = default;
            error = string.Empty;

            string remainder = query["update".Length..].TrimStart();
            if (!TryConsumeIdentifier(ref remainder, out collection))
            {
                error = "UPDATE collection name is missing or invalid.";
                return false;
            }

            if (!TryConsumeKeyword(ref remainder, "set"))
            {
                error = "UPDATE format must be: UPDATE <collection> SET <json-object> WHERE Id = <value>.";
                return false;
            }

            string trimmedRemainder = remainder.TrimStart();
            if (!trimmedRemainder.StartsWith("{", StringComparison.Ordinal))
            {
                error = "UPDATE requires a JSON object after SET.";
                return false;
            }

            int objectEndIndex = FindMatchingJsonObjectEndIndex(trimmedRemainder);
            if (objectEndIndex < 0)
            {
                error = "UPDATE JSON object is malformed.";
                return false;
            }

            string patchJson = trimmedRemainder[..(objectEndIndex + 1)];
            if (!TryParseJsonObject(patchJson, out patchPayload, out error))
            {
                return false;
            }

            string wherePart = trimmedRemainder[(objectEndIndex + 1)..].TrimStart();
            if (string.IsNullOrWhiteSpace(wherePart))
            {
                // Empty WHERE means "apply to all" and should be confirmed by the caller/UI.
                whereClause = string.Empty;
                return true;
            }

            if (!TryConsumeKeyword(ref wherePart, "where"))
            {
                error = "UPDATE trailing clause is invalid. Use optional WHERE <filterExpr>.";
                return false;
            }

            whereClause = (wherePart ?? string.Empty).Trim();
            return true;
        }

        private static bool TryParseDelete(string query, out string collection, out string whereClause, out string error)
        {
            collection = string.Empty;
            whereClause = string.Empty;
            error = string.Empty;

            string remainder = query["delete".Length..].TrimStart();
            if (!TryConsumeKeyword(ref remainder, "from"))
            {
                error = "DELETE format must be: DELETE FROM <collection> WHERE Id = <value>.";
                return false;
            }

            if (!TryConsumeIdentifier(ref remainder, out collection))
            {
                error = "DELETE collection name is missing or invalid.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(remainder))
            {
                // Empty WHERE means "apply to all" and should be confirmed by the caller/UI.
                whereClause = string.Empty;
                return true;
            }

            if (!TryConsumeKeyword(ref remainder, "where"))
            {
                error = "DELETE trailing clause is invalid. Use optional WHERE <filterExpr>.";
                return false;
            }

            whereClause = (remainder ?? string.Empty).Trim();
            return true;
        }

        private static Dictionary<string, object?> MergePatch(IReadOnlyDictionary<string, object?> existing, JsonElement patchPayload, string entityId)
        {
            // UPDATE is applied as a document merge so non-mentioned fields remain intact.
            Dictionary<string, object?> merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object?> item in existing)
            {
                merged[item.Key] = item.Value;
            }

            Dictionary<string, object?> patch = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(patchPayload.GetRawText()) ?? new Dictionary<string, object?>();
            foreach (KeyValuePair<string, object?> item in patch)
            {
                if (string.Equals(item.Key, "_id", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                merged[item.Key] = item.Value;
            }

            merged["Id"] = entityId;
            return merged;
        }

        private async Task<IReadOnlyList<string>> ResolveEntityIdsByWhereAsync(string collection, string whereClause, int take, CancellationToken cancellationToken)
        {
            // Resolve target ids via SELECT first, then apply writes through writer APIs for replication safety.
            string normalizedWhereClause = NormalizeWhereClause(whereClause);
            string lookupQuery = string.IsNullOrWhiteSpace(whereClause)
                ? $"SELECT $ FROM {collection} LIMIT {take}"
                : $"SELECT $ FROM {collection} WHERE {normalizedWhereClause} LIMIT {take}";
            IReadOnlyList<Dictionary<string, object?>> rows = await _reader.ExecuteQueryAsync<Dictionary<string, object?>>(lookupQuery, take, cancellationToken).ConfigureAwait(false);
            List<string> ids = new List<string>(rows.Count);

            foreach (Dictionary<string, object?> row in rows)
            {
                if (IsDeletedOrTombstoneRow(row))
                {
                    continue;
                }

                if (!TryExtractEntityId(row, out string? entityId))
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
                List<string> idPredicates = ExtractIdEqualityPredicates(normalizedWhereClause);
                if (idPredicates.Count > 0)
                {
                    foreach (string candidateId in idPredicates)
                    {
                        Dictionary<string, object?>? entity = await _reader.GetByIdAsync<Dictionary<string, object?>>(collection, candidateId, cancellationToken).ConfigureAwait(false);
                        if (entity is null)
                        {
                            continue;
                        }

                        if (ids.Contains(candidateId, StringComparer.Ordinal))
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
            }

            return ids;
        }

        private static bool ShouldFilterTombstonesForSelect(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            string normalized = query.Trim();
            if (TombstonePredicateRegex.IsMatch(normalized))
            {
                return false;
            }

            return true;
        }

        private static string NormalizeWhereClause(string whereClause)
        {
            string normalized = (whereClause ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return normalized;
            }

            normalized = DollarIdAliasRegex.Replace(normalized, "_id");
            normalized = IdAliasRegex.Replace(normalized, "_id");
            return normalized;
        }

        private static List<string> ExtractIdEqualityPredicates(string whereClause)
        {
            List<string> ids = new List<string>();
            MatchCollection matches = IdEqualityLiteralRegex.Matches(whereClause ?? string.Empty);
            foreach (Match match in matches)
            {
                string value = string.Empty;
                if (match.Groups[2].Success)
                {
                    value = match.Groups[2].Value;
                }
                else if (match.Groups[3].Success)
                {
                    value = match.Groups[3].Value;
                }
                else if (match.Groups[4].Success)
                {
                    value = match.Groups[4].Value;
                }

                value = value.Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (ids.Contains(value, StringComparer.Ordinal))
                {
                    continue;
                }

                ids.Add(value);
            }

            return ids;
        }

        private static bool TryExtractEntityId(IReadOnlyDictionary<string, object?> row, out string entityId)
        {
            entityId = string.Empty;

            foreach (KeyValuePair<string, object?> entry in row)
            {
                if (!string.Equals(entry.Key, "Id", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entry.Key, "_id", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entry.Key, "$_id", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entry.Key, "[value]", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.Value is null)
                {
                    return false;
                }

                string value = Convert.ToString(entry.Value)?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                entityId = value;
                return true;
            }

            return false;
        }

        private static bool IsDeletedOrTombstoneRow(IReadOnlyDictionary<string, object?> row)
        {
            if (TryReadBooleanLike(row, "_sys_deleted", out bool deleted) && deleted)
            {
                return true;
            }

            if (TryReadBooleanLike(row, "_sys_tombstone", out bool tombstone) && tombstone)
            {
                return true;
            }

            return false;
        }

        private static bool TryReadBooleanLike(IReadOnlyDictionary<string, object?> row, string key, out bool value)
        {
            value = false;

            foreach (KeyValuePair<string, object?> entry in row)
            {
                if (!string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.Value is bool boolValue)
                {
                    value = boolValue;
                    return true;
                }

                if (entry.Value is string stringValue && bool.TryParse(stringValue, out bool parsedString))
                {
                    value = parsedString;
                    return true;
                }

                if (entry.Value is JsonElement jsonElement)
                {
                    if (jsonElement.ValueKind == JsonValueKind.True)
                    {
                        value = true;
                        return true;
                    }

                    if (jsonElement.ValueKind == JsonValueKind.False)
                    {
                        value = false;
                        return true;
                    }

                    if (jsonElement.ValueKind == JsonValueKind.String && bool.TryParse(jsonElement.GetString(), out bool parsedJson))
                    {
                        value = parsedJson;
                        return true;
                    }
                }

                return false;
            }

            return false;
        }

        private static bool TryExtractEntityId(JsonElement payload, out string entityId)
        {
            entityId = string.Empty;
            if (payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonProperty property in payload.EnumerateObject())
            {
                if (!string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(property.Name, "_id", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    string? candidate = property.Value.GetString();
                    if (string.IsNullOrWhiteSpace(candidate))
                    {
                        return false;
                    }

                    entityId = candidate.Trim();
                    return true;
                }

                if (property.Value.ValueKind == JsonValueKind.Number)
                {
                    entityId = property.Value.GetRawText();
                    return true;
                }

                return false;
            }

            return false;
        }

        private static bool TryConsumeKeyword(ref string source, string keyword)
        {
            string working = source.TrimStart();
            if (!working.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (working.Length > keyword.Length && !char.IsWhiteSpace(working[keyword.Length]))
            {
                return false;
            }

            source = working[keyword.Length..].TrimStart();
            return true;
        }

        private static bool TryConsumeIdentifier(ref string source, out string identifier)
        {
            identifier = string.Empty;
            string working = source.TrimStart();
            if (working.Length == 0)
            {
                return false;
            }

            int splitIndex = working.IndexOfAny([' ', '\t', '\r', '\n']);
            if (splitIndex < 0)
            {
                if (!IdentifierRegex.IsMatch(working))
                {
                    return false;
                }

                identifier = working;
                source = string.Empty;
                return true;
            }

            string candidate = working[..splitIndex];
            if (!IdentifierRegex.IsMatch(candidate))
            {
                return false;
            }

            identifier = candidate;
            source = working[splitIndex..].TrimStart();
            return true;
        }

        private static bool TryParseJsonObject(string json, out JsonElement payload, out string error)
        {
            payload = default;
            error = string.Empty;
            string trimmed = (json ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                error = "JSON payload is required.";
                return false;
            }

            try
            {
                JsonDocument document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    document.Dispose();
                    error = "Payload must be a JSON object.";
                    return false;
                }

                payload = document.RootElement.Clone();
                document.Dispose();
                return true;
            }
            catch (JsonException ex)
            {
                error = $"Invalid JSON payload: {ex.Message}";
                return false;
            }
        }

        private static int FindMatchingJsonObjectEndIndex(string source)
        {
            int depth = 0;
            bool inString = false;
            bool escape = false;

            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];

                if (inString)
                {
                    if (escape)
                    {
                        escape = false;
                        continue;
                    }

                    if (c == '\\')
                    {
                        escape = true;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{')
                {
                    depth += 1;
                    continue;
                }

                if (c == '}')
                {
                    depth -= 1;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static bool TryGetCommand(string query, out string command)
        {
            command = string.Empty;
            Match match = FirstKeywordRegex.Match(query ?? string.Empty);
            if (!match.Success)
            {
                return false;
            }

            command = match.Groups["cmd"].Value;
            return true;
        }

        private static bool IsReservedCollection(string? collectionName)
        {
            return string.Equals(collectionName, Common.CacheCollectionName, StringComparison.OrdinalIgnoreCase);
        }

        public class QueryRequest
        {
            public string Query { get; set; } = string.Empty;
            public int Take { get; set; } = 200;
        }

        public class QueryResponse
        {
            public required string Query { get; set; }
            public required int RequestedTake { get; set; }
            public required int MatchedCount { get; set; }
            public required int AppliedCount { get; set; }
            public required int ReturnedRows { get; set; }
            public IReadOnlyList<Dictionary<string, object?>> Rows { get; set; } = Array.Empty<Dictionary<string, object?>>();
        }
    }
}
