using System.Text.Json;
using LiteDb.Distributed.Server.Core.Abstractions;
using LiteDb.Distributed.Server.Core.Cache;
using LiteDb.Distributed.Server.Core.Common;
using LiteDb.Distributed.Server.Core.Exceptions;
using LiteDb.Distributed.Server.Core.Models;
using LiteDb.Distributed.Server.Infrastructure.Cache;
using LiteDb.Distributed.Server.Infrastructure.Replication;
using LiteDb.Distributed.Server.Core.Filters;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [RequireClientDatabaseAuth]
    [Route("api/cache")]
    public class CacheController : ControllerBase
    {
        private readonly ILocalDocumentWriter _writer;
        private readonly ILocalDocumentReader _reader;
        private readonly IReplicationSignalPublisher _replicationSignalPublisher;
        private readonly ILogger<CacheController> _logger;

        public CacheController(ILocalDocumentWriter writer, ILocalDocumentReader reader, IReplicationSignalPublisher replicationSignalPublisher, ILogger<CacheController> logger)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _replicationSignalPublisher = replicationSignalPublisher ?? throw new ArgumentNullException(nameof(replicationSignalPublisher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpPut("{key}")]
        public async Task<IActionResult> SetAsync(string key, [FromBody] JsonElement value, [FromQuery(Name = "ttl")] string? ttl, CancellationToken cancellationToken)
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (!CachePolicy.TryNormalizeKey(key, out string normalizedKey, out string keyError))
            {
                stopwatch.Stop();
                _logger.LogWarning("Cache set rejected due to invalid key. Key={Key} DurationMs={DurationMs}", key, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = keyError });
            }

            if (!CachePolicy.TryParseTtl(ttl, out TimeSpan ttlValue, out string ttlError))
            {
                stopwatch.Stop();
                _logger.LogWarning("Cache set rejected due to invalid ttl. Key={Key} Ttl={Ttl} DurationMs={DurationMs}", normalizedKey, ttl, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = ttlError });
            }

            DateTime now = DateTime.UtcNow;
            CacheEntryDocument? existing = await _reader.GetByIdAsync<CacheEntryDocument>(Common.CacheCollectionName, normalizedKey, cancellationToken).ConfigureAwait(false);
            DateTime createdUtc = existing is not null && !CachePolicy.IsExpired(existing, now) ? CachePolicy.NormalizeUtc(existing.CreatedUtc) : now;
            DateTime expiresAtUtc = now.Add(ttlValue);
            CacheEntryDocument document = new CacheEntryDocument
            {
                Id = normalizedKey,
                Key = normalizedKey,
                Value = value.Clone(),
                CreatedUtc = createdUtc,
                UpdatedUtc = now,
                ExpiresAtUtc = expiresAtUtc
            };

            try
            {
                WriteResult result = await _writer.UpsertAsync(Common.CacheCollectionName, normalizedKey, document, cancellationToken: cancellationToken).ConfigureAwait(false);
                _replicationSignalPublisher.NotifyLocalChange("cache-upsert");
                stopwatch.Stop();

                _logger.LogDebug("Cache set applied. Key={Key} TtlMs={TtlMs} ExpiresAtUtc={ExpiresAtUtc} Version={Version} DurationMs={DurationMs}", normalizedKey, ttlValue.TotalMilliseconds, expiresAtUtc, result.Version, stopwatch.Elapsed.TotalMilliseconds);

                return Ok(new CacheSetResponse
                {
                    Key = normalizedKey,
                    Version = result.Version,
                    CommittedUtc = result.CommittedUtc,
                    ExpiresAtUtc = expiresAtUtc,
                    Ttl = CachePolicy.FormatTtl(ttlValue)
                });
            }
            catch (VersionMismatchException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Cache set conflict. Key={Key} DurationMs={DurationMs}", normalizedKey, stopwatch.Elapsed.TotalMilliseconds);
                return Conflict(new { Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Cache set rejected. Key={Key} DurationMs={DurationMs}", normalizedKey, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = ex.Message });
            }
        }

        [HttpGet("{key}")]
        public async Task<IActionResult> GetAsync(string key, CancellationToken cancellationToken)
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (!CachePolicy.TryNormalizeKey(key, out string normalizedKey, out string keyError))
            {
                stopwatch.Stop();
                _logger.LogWarning("Cache get rejected due to invalid key. Key={Key} DurationMs={DurationMs}", key, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = keyError });
            }

            CacheEntryDocument? entry = await _reader.GetByIdAsync<CacheEntryDocument>(Common.CacheCollectionName, normalizedKey, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                stopwatch.Stop();
                _logger.LogDebug("Cache get miss. Key={Key} DurationMs={DurationMs}", normalizedKey, stopwatch.Elapsed.TotalMilliseconds);
                return NotFound();
            }

            DateTime now = DateTime.UtcNow;
            if (CachePolicy.IsExpired(entry, now))
            {
                await _writer.DeleteAsync(Common.CacheCollectionName, normalizedKey, cancellationToken: cancellationToken).ConfigureAwait(false);
                _replicationSignalPublisher.NotifyLocalChange("cache-expired-delete");
                stopwatch.Stop();

                _logger.LogDebug("Cache get found expired entry and removed it. Key={Key} ExpiresAtUtc={ExpiresAtUtc} DurationMs={DurationMs}", normalizedKey, entry.ExpiresAtUtc, stopwatch.Elapsed.TotalMilliseconds);

                return NotFound();
            }

            stopwatch.Stop();
            _logger.LogDebug("Cache get hit. Key={Key} ExpiresAtUtc={ExpiresAtUtc} DurationMs={DurationMs}", normalizedKey, entry.ExpiresAtUtc, stopwatch.Elapsed.TotalMilliseconds);

            return Ok(new CacheGetResponse
            {
                Key = normalizedKey,
                Value = entry.Value,
                CreatedUtc = CachePolicy.NormalizeUtc(entry.CreatedUtc),
                UpdatedUtc = CachePolicy.NormalizeUtc(entry.UpdatedUtc),
                ExpiresAtUtc = CachePolicy.NormalizeUtc(entry.ExpiresAtUtc),
                RemainingTtl = CachePolicy.FormatTtl(entry.ExpiresAtUtc - now)
            });
        }

        [HttpDelete("{key}")]
        public async Task<IActionResult> DeleteAsync(string key, CancellationToken cancellationToken)
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (!CachePolicy.TryNormalizeKey(key, out string normalizedKey, out string keyError))
            {
                stopwatch.Stop();
                _logger.LogWarning("Cache delete rejected due to invalid key. Key={Key} DurationMs={DurationMs}", key, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = keyError });
            }

            try
            {
                WriteResult result = await _writer.DeleteAsync(Common.CacheCollectionName, normalizedKey, cancellationToken: cancellationToken).ConfigureAwait(false);
                _replicationSignalPublisher.NotifyLocalChange("cache-delete");
                stopwatch.Stop();

                _logger.LogDebug("Cache delete applied. Key={Key} Version={Version} DurationMs={DurationMs}", normalizedKey, result.Version, stopwatch.Elapsed.TotalMilliseconds);

                return Ok(result);
            }
            catch (VersionMismatchException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Cache delete conflict. Key={Key} DurationMs={DurationMs}", normalizedKey, stopwatch.Elapsed.TotalMilliseconds);
                return Conflict(new { Error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                stopwatch.Stop();
                _logger.LogWarning(ex, "Cache delete rejected. Key={Key} DurationMs={DurationMs}", normalizedKey, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = ex.Message });
            }
        }

    }
}
