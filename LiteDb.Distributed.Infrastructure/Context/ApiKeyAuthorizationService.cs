using System.Security.Cryptography;
using System.Text;

namespace LiteDb.Distributed.Infrastructure.Context
{
    public class ApiKeyAuthorizationService : IApiKeyAuthorizationService
    {
        private const string AddDbRole = "ADD_DB";
        private const string DeleteDbRole = "DELETE_DB";
        private const string ReadDocumentRole = "READ_DOCUMENT";
        private const string WriteDocumentRole = "WRITE_DOCUMENT";
        private const string UpdateDocumentRole = "UPDATE_DOCUMENT";
        private const string DeleteDocumentRole = "DELETE_DOCUMENT";

        private readonly ApiKeyAuthorizationOptions _options;
        private readonly List<ApiKeyEntryOptions> _entries;

        public ApiKeyAuthorizationService(ApiKeyAuthorizationOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            RootApiKey = NormalizeApiKey(_options.RootApiKey);
            _entries = (_options.ApiKeys ?? new List<ApiKeyEntryOptions>())
                .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Key))
                .ToList();
        }

        public string RootApiKey { get; }

        public ApiKeyAccess Authorize(string apiKey, string databaseName)
        {
            string normalizedApiKey = NormalizeApiKey(apiKey);
            string normalizedDatabase = DatabaseNameNormalizer.Normalize(databaseName);

            if (SecureEquals(normalizedApiKey, RootApiKey))
            {
                return new ApiKeyAccess
                {
                    ApiKey = RootApiKey,
                    IsRoot = true,
                    CanAddDatabase = true,
                    CanDeleteDatabase = true,
                    CanReadDocument = true,
                    CanWriteDocument = true,
                    CanUpdateDocument = true,
                    CanDeleteDocument = true
                };
            }

            ApiKeyEntryOptions? entry = _entries.FirstOrDefault(candidate => SecureEquals(NormalizeApiKey(candidate.Key), normalizedApiKey));
            if (entry is null)
            {
                throw new UnauthorizedAccessException("ApiKey is invalid.");
            }

            bool hasWildcard = entry.Databases.Any(db => string.Equals(db?.Trim(), "*", StringComparison.Ordinal));
            bool hasDatabaseAccess = hasWildcard || entry.Databases.Any(db => string.Equals(DatabaseNameNormalizer.Normalize(db ?? string.Empty), normalizedDatabase, StringComparison.Ordinal));
            if (!hasDatabaseAccess)
            {
                throw new UnauthorizedAccessException($"ApiKey does not have access to database '{normalizedDatabase}'.");
            }

            Dictionary<string, bool> roles = entry.Roles ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            return new ApiKeyAccess
            {
                ApiKey = normalizedApiKey,
                IsRoot = false,
                CanAddDatabase = IsRoleEnabled(roles, AddDbRole),
                CanDeleteDatabase = IsRoleEnabled(roles, DeleteDbRole),
                CanReadDocument = IsRoleEnabled(roles, ReadDocumentRole),
                CanWriteDocument = IsRoleEnabled(roles, WriteDocumentRole),
                CanUpdateDocument = IsRoleEnabled(roles, UpdateDocumentRole),
                CanDeleteDocument = IsRoleEnabled(roles, DeleteDocumentRole)
            };
        }

        private static string NormalizeApiKey(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("ApiKey header is required.", nameof(apiKey));
            }

            return apiKey.Trim();
        }

        private static bool IsRoleEnabled(IReadOnlyDictionary<string, bool> roles, string role)
        {
            return roles.TryGetValue(role, out bool enabled) && enabled;
        }

        private static bool SecureEquals(string left, string right)
        {
            byte[] leftBytes = Encoding.UTF8.GetBytes(left);
            byte[] rightBytes = Encoding.UTF8.GetBytes(right);
            return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
    }
}
