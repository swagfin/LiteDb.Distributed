using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteDb.Distributed.Server.Infrastructure.Queries
{
    internal static class QueryRowHelpers
    {
        private static readonly Regex IdEqualityLiteralRegex = new("_id\\s*=\\s*('([^']*)'|\"([^\"]*)\"|([A-Za-z0-9_\\-]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static Dictionary<string, object?> MergePatch(IReadOnlyDictionary<string, object?> existing, JsonElement patchPayload, string entityId)
        {
            // UPDATE is applied as a document merge so non-mentioned fields remain intact.
            Dictionary<string, object?> merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object?> item in existing)
            {
                merged[item.Key] = item.Value;
            }

            Dictionary<string, object?> patch = JsonSerializer.Deserialize<Dictionary<string, object?>>(patchPayload.GetRawText()) ?? new Dictionary<string, object?>();
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

        public static List<string> ExtractIdEqualityPredicates(string whereClause)
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

        public static bool TryExtractEntityId(IReadOnlyDictionary<string, object?> row, out string entityId)
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

        public static bool TryExtractEntityId(JsonElement payload, out string entityId)
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

        public static bool IsDeletedOrTombstoneRow(IReadOnlyDictionary<string, object?> row)
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
    }
}
