using System.Globalization;
using System.Text.Json;
using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Exceptions;
using LiteDb.Distributed.Infrastructure.Replication;
using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Controllers
{
    [ApiController]
    [Route("api/cache")]
    public class CacheController : ControllerBase
    {
        private const string CacheCollectionName = "cache";
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MaximumTtl = TimeSpan.FromDays(30);

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

            if (!TryNormalizeKey(key, out string? normalizedKey, out string? keyError))
            {
                stopwatch.Stop();
                _logger.LogWarning("Cache set rejected due to invalid key. Key={Key} DurationMs={DurationMs}", key, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = keyError });
            }

            if (!TryParseTtl(ttl, out TimeSpan ttlValue, out string? ttlError))
            {
                stopwatch.Stop();
                _logger.LogWarning("Cache set rejected due to invalid ttl. Key={Key} Ttl={Ttl} DurationMs={DurationMs}", normalizedKey, ttl, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = ttlError });
            }

            DateTime now = DateTime.UtcNow;
            CacheEntryDocument? existing = await _reader.GetByIdAsync<CacheEntryDocument>(CacheCollectionName, normalizedKey, cancellationToken).ConfigureAwait(false);
            DateTime createdUtc = existing is not null && !IsExpired(existing, now) ? NormalizeUtc(existing.CreatedUtc) : now;
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
                Core.Models.WriteResult result = await _writer.UpsertAsync(CacheCollectionName, normalizedKey, document, cancellationToken: cancellationToken).ConfigureAwait(false);
                _replicationSignalPublisher.NotifyLocalChange("cache-upsert");
                stopwatch.Stop();

                _logger.LogInformation("Cache set applied. Key={Key} TtlMs={TtlMs} ExpiresAtUtc={ExpiresAtUtc} Version={Version} DurationMs={DurationMs}", normalizedKey, ttlValue.TotalMilliseconds, expiresAtUtc, result.Version, stopwatch.Elapsed.TotalMilliseconds);

                return Ok(new CacheSetResponse
                {
                    Key = normalizedKey,
                    Version = result.Version,
                    CommittedUtc = result.CommittedUtc,
                    ExpiresAtUtc = expiresAtUtc,
                    Ttl = FormatTtl(ttlValue)
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

            if (!TryNormalizeKey(key, out string? normalizedKey, out string? keyError))
            {
                stopwatch.Stop();
                _logger.LogWarning("Cache get rejected due to invalid key. Key={Key} DurationMs={DurationMs}", key, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = keyError });
            }

            CacheEntryDocument? entry = await _reader.GetByIdAsync<CacheEntryDocument>(CacheCollectionName, normalizedKey, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                stopwatch.Stop();
                _logger.LogDebug("Cache get miss. Key={Key} DurationMs={DurationMs}", normalizedKey, stopwatch.Elapsed.TotalMilliseconds);
                return NotFound();
            }

            DateTime now = DateTime.UtcNow;
            if (IsExpired(entry, now))
            {
                await _writer.DeleteAsync(CacheCollectionName, normalizedKey, cancellationToken: cancellationToken).ConfigureAwait(false);
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
                CreatedUtc = NormalizeUtc(entry.CreatedUtc),
                UpdatedUtc = NormalizeUtc(entry.UpdatedUtc),
                ExpiresAtUtc = NormalizeUtc(entry.ExpiresAtUtc),
                RemainingTtl = FormatTtl(entry.ExpiresAtUtc - now)
            });
        }

        [HttpDelete("{key}")]
        public async Task<IActionResult> DeleteAsync(string key, CancellationToken cancellationToken)
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (!TryNormalizeKey(key, out string? normalizedKey, out string? keyError))
            {
                stopwatch.Stop();
                _logger.LogWarning("Cache delete rejected due to invalid key. Key={Key} DurationMs={DurationMs}", key, stopwatch.Elapsed.TotalMilliseconds);
                return BadRequest(new { Error = keyError });
            }

            try
            {
                Core.Models.WriteResult result = await _writer.DeleteAsync(CacheCollectionName, normalizedKey, cancellationToken: cancellationToken).ConfigureAwait(false);
                _replicationSignalPublisher.NotifyLocalChange("cache-delete");
                stopwatch.Stop();

                _logger.LogInformation("Cache delete applied. Key={Key} Version={Version} DurationMs={DurationMs}", normalizedKey, result.Version, stopwatch.Elapsed.TotalMilliseconds);

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

        private static bool IsExpired(CacheEntryDocument document, DateTime utcNow)
        {
            return NormalizeUtc(document.ExpiresAtUtc) <= utcNow;
        }

        private static bool TryNormalizeKey(string key, out string normalizedKey, out string error)
        {
            normalizedKey = key?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                error = "Cache key is required.";
                return false;
            }

            if (normalizedKey.Length > 256)
            {
                error = "Cache key cannot exceed 256 characters.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryParseTtl(string? ttl, out TimeSpan ttlValue, out string error)
        {
            if (string.IsNullOrWhiteSpace(ttl))
            {
                ttlValue = DefaultTtl;
                error = string.Empty;
                return true;
            }

            string input = ttl.Trim();
            if (!TryParseTtlInternal(input, out ttlValue))
            {
                error = "Invalid ttl format. Use values like '30s', '5m', '2h', or '1d'.";
                return false;
            }

            if (ttlValue <= TimeSpan.Zero)
            {
                error = "ttl must be greater than zero.";
                return false;
            }

            if (ttlValue > MaximumTtl)
            {
                error = $"ttl cannot exceed {FormatTtl(MaximumTtl)}.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryParseTtlInternal(string ttl, out TimeSpan ttlValue)
        {
            ttlValue = default;

            if (TryParseUnitSuffixedTtl(ttl, out ttlValue))
            {
                return true;
            }

            if (TimeSpan.TryParse(ttl, CultureInfo.InvariantCulture, out TimeSpan parsed))
            {
                ttlValue = parsed;
                return true;
            }

            if (double.TryParse(ttl, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                ttlValue = TimeSpan.FromSeconds(seconds);
                return true;
            }

            return false;
        }

        private static bool TryParseUnitSuffixedTtl(string ttl, out TimeSpan ttlValue)
        {
            ttlValue = default;
            if (ttl.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseNumericDuration(ttl[..^2], TimeSpan.FromMilliseconds, out ttlValue);
            }

            if (ttl.Length < 2)
            {
                return false;
            }

            char suffix = char.ToLowerInvariant(ttl[^1]);
            string numericPart = ttl[..^1];
            return suffix switch
            {
                's' => TryParseNumericDuration(numericPart, TimeSpan.FromSeconds, out ttlValue),
                'm' => TryParseNumericDuration(numericPart, TimeSpan.FromMinutes, out ttlValue),
                'h' => TryParseNumericDuration(numericPart, TimeSpan.FromHours, out ttlValue),
                'd' => TryParseNumericDuration(numericPart, TimeSpan.FromDays, out ttlValue),
                _ => false
            };
        }

        private static bool TryParseNumericDuration(string value, Func<double, TimeSpan> builder, out TimeSpan ttlValue)
        {
            ttlValue = default;

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double numericValue))
            {
                return false;
            }

            try
            {
                ttlValue = builder(numericValue);
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static string FormatTtl(TimeSpan ttl)
        {
            TimeSpan safe = ttl <= TimeSpan.Zero ? TimeSpan.Zero : ttl;
            return $"{Math.Ceiling(safe.TotalMilliseconds)}ms";
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            return value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private class CacheEntryDocument
        {
            public string Id { get; init; } = string.Empty;
            public string Key { get; init; } = string.Empty;
            public JsonElement Value { get; init; }
            public DateTime CreatedUtc { get; init; }
            public DateTime UpdatedUtc { get; init; }
            public DateTime ExpiresAtUtc { get; init; }
        }

        private class CacheSetResponse
        {
            public required string Key { get; init; }
            public required string Version { get; init; }
            public required DateTime CommittedUtc { get; init; }
            public required DateTime ExpiresAtUtc { get; init; }
            public required string Ttl { get; init; }
        }

        private class CacheGetResponse
        {
            public required string Key { get; init; }
            public required JsonElement Value { get; init; }
            public required DateTime CreatedUtc { get; init; }
            public required DateTime UpdatedUtc { get; init; }
            public required DateTime ExpiresAtUtc { get; init; }
            public required string RemainingTtl { get; init; }
        }
    }


}
