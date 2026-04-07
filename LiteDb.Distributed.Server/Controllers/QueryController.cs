using System.Text.RegularExpressions;
using LiteDb.Distributed.Core.Abstractions;
using LiteDB;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [Route("api/query")]
    public class QueryController : ControllerBase
    {
        private static readonly Regex FirstKeywordRegex = new("^(?<cmd>[a-zA-Z]+)", RegexOptions.Compiled);
        private static readonly Regex IntoRegex = new("\\binto\\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly ILocalDocumentReader _reader;
        private readonly ILogger<QueryController> _logger;

        public QueryController(ILocalDocumentReader reader, ILogger<QueryController> logger)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpPost]
        public async Task<IActionResult> ExecuteAsync([FromBody] QueryRequest request, CancellationToken cancellationToken)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Query))
            {
                return BadRequest(new { Error = "Query is required." });
            }

            if (!TryNormalizeReadOnlyQuery(request.Query, out string? normalizedQuery, out string? validationError))
            {
                return BadRequest(new { Error = validationError });
            }

            // Hard cap protects server memory from unbounded result-set requests.
            int safeTake = request.Take <= 0 ? 100 : Math.Clamp(request.Take, 1, 10_000);

            try
            {
                IReadOnlyList<Dictionary<string, object?>> rows = await _reader.ExecuteQueryAsync<Dictionary<string, object?>>(normalizedQuery, safeTake, cancellationToken).ConfigureAwait(false);

                return Ok(new QueryResponse
                {
                    Query = normalizedQuery,
                    RequestedTake = safeTake,
                    ReturnedRows = rows.Count,
                    Rows = rows
                });
            }
            catch (LiteException ex)
            {
                _logger.LogWarning(ex, "Read-only query execution failed.");
                return BadRequest(new { Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Read-only query rejected.");
                return BadRequest(new { Error = ex.Message });
            }
        }

        private static bool TryNormalizeReadOnlyQuery(string rawQuery, out string normalizedQuery, out string error)
        {
            string query = (rawQuery ?? string.Empty).Trim();

            // Allow optional trailing semicolon while still enforcing single-statement execution.
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
                error = "Only one SELECT/EXPLAIN statement is allowed.";
                return false;
            }

            Match match = FirstKeywordRegex.Match(query);
            if (!match.Success)
            {
                normalizedQuery = string.Empty;
                error = "Unable to determine query command.";
                return false;
            }

            string command = match.Groups["cmd"].Value;
            if (!string.Equals(command, "select", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(command, "explain", StringComparison.OrdinalIgnoreCase))
            {
                normalizedQuery = string.Empty;
                error = "Only SELECT or EXPLAIN queries are allowed in this endpoint.";
                return false;
            }

            if (IntoRegex.IsMatch(query))
            {
                // SELECT INTO writes data, so it is blocked for this read-only endpoint.
                normalizedQuery = string.Empty;
                error = "SELECT INTO is not allowed in this endpoint.";
                return false;
            }

            normalizedQuery = query;
            error = string.Empty;
            return true;
        }

        public class QueryRequest
        {
            public string Query { get; init; } = string.Empty;
            public int Take { get; init; } = 200;
        }

        public class QueryResponse
        {
            public required string Query { get; init; }
            public required int RequestedTake { get; init; }
            public required int ReturnedRows { get; init; }
            public IReadOnlyList<Dictionary<string, object?>> Rows { get; init; } = Array.Empty<Dictionary<string, object?>>();
        }
    }

}
