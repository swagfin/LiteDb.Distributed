using System.Text.RegularExpressions;

namespace LiteDb.Distributed.Infrastructure.Context
{
    internal static class DatabaseNameNormalizer
    {
        private static readonly Regex AllowedPattern = new("^[a-z0-9][a-z0-9_-]{0,62}$", RegexOptions.Compiled);

        public static string Normalize(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                throw new ArgumentException("Database header is required.", nameof(rawName));
            }

            string normalized = rawName.Trim().ToLowerInvariant();

            if (!AllowedPattern.IsMatch(normalized))
            {
                throw new ArgumentException(
                    "Database name contains invalid characters. Use lowercase letters, numbers, '-' or '_' only.",
                    nameof(rawName));
            }

            return normalized;
        }
    }

}
