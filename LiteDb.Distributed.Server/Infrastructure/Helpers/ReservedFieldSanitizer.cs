using LiteDb.Distributed.Server.Core.Common;

namespace LiteDb.Distributed.Server.Infrastructure.Helpers
{
    public static class ReservedFieldSanitizer
    {
        public static IReadOnlyList<Dictionary<string, object?>> SanitizeRowsIfNeeded(IReadOnlyList<Dictionary<string, object?>> rows)
        {
            List<Dictionary<string, object?>>? sanitized = null;

            for (int i = 0; i < rows.Count; i++)
            {
                Dictionary<string, object?> row = rows[i];
                if (!RequiresSanitization(row))
                {
                    sanitized?.Add(row);
                    continue;
                }

                if (sanitized is null)
                {
                    sanitized = new List<Dictionary<string, object?>>(rows.Count);
                    for (int j = 0; j < i; j++)
                    {
                        sanitized.Add(rows[j]);
                    }
                }

                sanitized.Add(SanitizeRow(row));
            }

            return sanitized ?? rows;
        }

        public static bool RequiresSanitization(IReadOnlyDictionary<string, object?> source)
        {
            foreach (KeyValuePair<string, object?> entry in source)
            {
                if (string.Equals(entry.Key, Common.InternalIdField, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (entry.Key.StartsWith(Common.SystemPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static Dictionary<string, object?> SanitizeRow(IReadOnlyDictionary<string, object?> source)
        {
            Dictionary<string, object?> sanitized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            object? internalIdValue = null;

            foreach (KeyValuePair<string, object?> entry in source)
            {
                if (string.Equals(entry.Key, Common.InternalIdField, StringComparison.OrdinalIgnoreCase))
                {
                    internalIdValue = entry.Value;
                    continue;
                }

                if (entry.Key.StartsWith(Common.SystemPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sanitized[entry.Key] = entry.Value;
            }

            if (!sanitized.ContainsKey(Common.IdField) && internalIdValue is not null)
            {
                sanitized[Common.IdField] = internalIdValue;
            }

            return sanitized;
        }
    }
}
