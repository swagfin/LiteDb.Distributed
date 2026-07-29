using System.Text.Json;
using System.Text.RegularExpressions;
using LiteDb.Distributed.Server.Core.Queries;

namespace LiteDb.Distributed.Server.Infrastructure.Queries
{
    internal static class SafeQueryParser
    {
        private static readonly Regex FirstKeywordRegex = new("^(?<cmd>[a-zA-Z]+)", RegexOptions.Compiled);
        private static readonly Regex IdentifierRegex = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

        public static bool TryNormalizeSingleStatementQuery(string rawQuery, out string normalizedQuery, out string error)
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

            if (!TryGetCommand(query, out QueryCommand _))
            {
                normalizedQuery = string.Empty;
                error = "Unable to determine query command.";
                return false;
            }

            normalizedQuery = query;
            error = string.Empty;
            return true;
        }

        public static bool TryGetCommand(string query, out QueryCommand command)
        {
            command = default;
            Match match = FirstKeywordRegex.Match(query ?? string.Empty);
            if (!match.Success)
            {
                return false;
            }

            string keyword = match.Groups["cmd"].Value;
            if (string.Equals(keyword, "select", StringComparison.OrdinalIgnoreCase))
            {
                command = QueryCommand.Select;
                return true;
            }

            if (string.Equals(keyword, "insert", StringComparison.OrdinalIgnoreCase))
            {
                command = QueryCommand.Insert;
                return true;
            }

            if (string.Equals(keyword, "update", StringComparison.OrdinalIgnoreCase))
            {
                command = QueryCommand.Update;
                return true;
            }

            if (string.Equals(keyword, "delete", StringComparison.OrdinalIgnoreCase))
            {
                command = QueryCommand.Delete;
                return true;
            }

            return false;
        }

        public static bool TryParseInsert(string query, out string collection, out JsonElement payload, out string error)
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

            return TryParseJsonObject(remainder, out payload, out error);
        }

        public static bool TryParseUpdate(string query, out string collection, out string whereClause, out JsonElement patchPayload, out string error)
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

        public static bool TryParseDelete(string query, out string collection, out string whereClause, out string error)
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
    }
}
