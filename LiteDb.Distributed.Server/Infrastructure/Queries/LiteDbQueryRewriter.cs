using System.Text.RegularExpressions;
using LiteDb.Distributed.Server.Data.Internal.Schema;

namespace LiteDb.Distributed.Server.Infrastructure.Queries
{
    internal static class LiteDbQueryRewriter
    {
        private static readonly Regex DollarIdAliasRegex = new("(?<![A-Za-z0-9_])\\$_id(?![A-Za-z0-9_])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex IdAliasRegex = new("(?<![A-Za-z0-9_])id(?![A-Za-z0-9_])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TombstonePredicateRegex = new("(_sys_deleted|_sys_tombstone)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SelectFromCollectionRegex = new("\\bfrom\\s+(?<collection>[A-Za-z0-9_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SelectTopRegex = new("^select\\s+top\\s+(?<take>\\d+)\\s+(?<body>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex LimitClauseRegex = new("\\blimit\\s+(?<take>\\d+)\\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public const string LiveDocumentPredicate = "_sys_deleted = false AND _sys_tombstone = false";

        public static string NormalizeSelectTopSyntax(string query, out int? declaredTake)
        {
            declaredTake = null;
            Match match = SelectTopRegex.Match(query ?? string.Empty);
            if (!match.Success)
            {
                return query ?? string.Empty;
            }

            if (int.TryParse(match.Groups["take"].Value, out int topTake))
            {
                declaredTake = Math.Clamp(topTake, 1, 10_000);
            }

            string body = match.Groups["body"].Value.Trim();
            return $"SELECT {body} LIMIT {declaredTake ?? 100}";
        }

        public static bool TryReadLimitTake(string query, out int take)
        {
            take = 0;
            MatchCollection matches = LimitClauseRegex.Matches(query ?? string.Empty);
            if (matches.Count == 0)
            {
                return false;
            }

            Match match = matches[^1];
            if (!int.TryParse(match.Groups["take"].Value, out int parsedTake))
            {
                return false;
            }

            take = Math.Clamp(parsedTake, 1, 10_000);
            return true;
        }

        public static string AddLiveDocumentPredicateToSelect(string query)
        {
            if (!ShouldFilterTombstonesForSelect(query))
            {
                return query;
            }

            if (!TryGetSelectCollection(query, out string collection) || LiteDbSystemCollections.IsSystemCollection(collection))
            {
                return query;
            }

            int fromIndex = SelectFromCollectionRegex.Match(query).Index;
            int tailIndex = FindFirstTopLevelClauseIndex(query, fromIndex, "order by", "limit", "offset");
            int searchEndIndex = tailIndex < 0 ? query.Length : tailIndex;
            int whereIndex = FindTopLevelClauseIndex(query, "where", fromIndex, searchEndIndex);

            if (whereIndex < 0)
            {
                string head = query[..searchEndIndex].TrimEnd();
                string tail = query[searchEndIndex..].TrimStart();
                return string.IsNullOrWhiteSpace(tail)
                    ? $"{head} WHERE {LiveDocumentPredicate}"
                    : $"{head} WHERE {LiveDocumentPredicate} {tail}";
            }

            string prefix = query[..whereIndex].TrimEnd();
            string whereBody = query[(whereIndex + "where".Length)..searchEndIndex].Trim();
            string trailingClause = query[searchEndIndex..].TrimStart();

            if (string.IsNullOrWhiteSpace(whereBody))
            {
                return query;
            }

            return string.IsNullOrWhiteSpace(trailingClause)
                ? $"{prefix} WHERE {LiveDocumentPredicate} AND ({whereBody})"
                : $"{prefix} WHERE {LiveDocumentPredicate} AND ({whereBody}) {trailingClause}";
        }

        public static bool ShouldFilterTombstonesForSelect(string query)
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

        public static string NormalizeWhereClause(string whereClause)
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

        private static bool TryGetSelectCollection(string query, out string collection)
        {
            collection = string.Empty;
            Match match = SelectFromCollectionRegex.Match(query ?? string.Empty);
            if (!match.Success)
            {
                return false;
            }

            collection = match.Groups["collection"].Value;
            return true;
        }

        private static int FindFirstTopLevelClauseIndex(string source, int startIndex, params string[] clauses)
        {
            int result = -1;
            foreach (string clause in clauses)
            {
                int clauseIndex = FindTopLevelClauseIndex(source, clause, startIndex, source.Length);
                if (clauseIndex >= 0 && (result < 0 || clauseIndex < result))
                {
                    result = clauseIndex;
                }
            }

            return result;
        }

        private static int FindTopLevelClauseIndex(string source, string clause, int startIndex, int endIndex)
        {
            bool inSingleQuotedString = false;
            bool inDoubleQuotedString = false;
            int depth = 0;
            int safeStartIndex = Math.Max(0, startIndex);
            int safeEndIndex = Math.Min(source.Length, endIndex);

            for (int i = safeStartIndex; i < safeEndIndex; i++)
            {
                char c = source[i];
                if (c == '\'' && !inDoubleQuotedString)
                {
                    inSingleQuotedString = !inSingleQuotedString;
                    continue;
                }

                if (c == '"' && !inSingleQuotedString)
                {
                    inDoubleQuotedString = !inDoubleQuotedString;
                    continue;
                }

                if (inSingleQuotedString || inDoubleQuotedString)
                {
                    continue;
                }

                if (c == '(')
                {
                    depth += 1;
                    continue;
                }

                if (c == ')' && depth > 0)
                {
                    depth -= 1;
                    continue;
                }

                if (depth == 0 && IsClauseAt(source, clause, i, safeEndIndex))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsClauseAt(string source, string clause, int index, int endIndex)
        {
            if (!IsWordBoundaryBefore(source, index))
            {
                return false;
            }

            string[] words = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int cursor = index;
            for (int i = 0; i < words.Length; i++)
            {
                string word = words[i];
                if (cursor + word.Length > endIndex || !source.AsSpan(cursor, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                cursor += word.Length;
                if (i < words.Length - 1)
                {
                    if (cursor >= endIndex || !char.IsWhiteSpace(source[cursor]))
                    {
                        return false;
                    }

                    while (cursor < endIndex && char.IsWhiteSpace(source[cursor]))
                    {
                        cursor += 1;
                    }
                }
            }

            return IsWordBoundaryAfter(source, cursor);
        }

        private static bool IsWordBoundaryBefore(string source, int index)
        {
            return index <= 0 || !IsIdentifierChar(source[index - 1]);
        }

        private static bool IsWordBoundaryAfter(string source, int index)
        {
            return index >= source.Length || !IsIdentifierChar(source[index]);
        }

        private static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '$';
        }
    }
}
