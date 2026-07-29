using System.Globalization;
using LiteDb.Distributed.Server.Core.Cache;

namespace LiteDb.Distributed.Server.Infrastructure.Cache
{
    internal static class CachePolicy
    {
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MaximumTtl = TimeSpan.FromDays(30);

        public static bool IsExpired(CacheEntryDocument document, DateTime utcNow)
        {
            return NormalizeUtc(document.ExpiresAtUtc) <= utcNow;
        }

        public static bool TryNormalizeKey(string key, out string normalizedKey, out string error)
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

        public static bool TryParseTtl(string? ttl, out TimeSpan ttlValue, out string error)
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

        public static string FormatTtl(TimeSpan ttl)
        {
            TimeSpan safe = ttl <= TimeSpan.Zero ? TimeSpan.Zero : ttl;
            return $"{Math.Ceiling(safe.TotalMilliseconds)}ms";
        }

        public static DateTime NormalizeUtc(DateTime value)
        {
            return value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
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
    }
}
