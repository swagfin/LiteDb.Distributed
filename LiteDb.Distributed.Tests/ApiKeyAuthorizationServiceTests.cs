using LiteDb.Distributed.Infrastructure.Context;

namespace LiteDb.Distributed.Tests
{
    public class ApiKeyAuthorizationServiceTests
    {
        [Fact]
        public void Authorize_ReturnsRootAccess_WhenRootApiKeyIsProvided()
        {
            ApiKeyAuthorizationService service = CreateService();

            // Root key is the cluster-wide super key and should map to full permissions.
            ApiKeyAccess access = service.Authorize("root", "testapp");

            Assert.True(access.IsRoot);
            Assert.True(access.CanAddDatabase);
            Assert.True(access.CanDeleteDatabase);
            Assert.True(access.CanReadDocument);
            Assert.True(access.CanWriteDocument);
            Assert.True(access.CanUpdateDocument);
            Assert.True(access.CanDeleteDocument);
        }

        [Fact]
        public void Authorize_Throws_WhenApiKeyIsInvalid()
        {
            ApiKeyAuthorizationService service = CreateService();

            // Unknown key should be rejected before any role checks are evaluated.
            UnauthorizedAccessException ex = Assert.Throws<UnauthorizedAccessException>(() => service.Authorize("invalid-key", "testapp"));

            Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Authorize_Throws_WhenDatabaseIsNotInScope()
        {
            ApiKeyAuthorizationService service = CreateService();

            // Key exists, but it is scoped to a different database list.
            UnauthorizedAccessException ex = Assert.Throws<UnauthorizedAccessException>(() => service.Authorize("key-123", "otherdb"));

            Assert.Contains("does not have access", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Authorize_ReturnsConfiguredRoleFlags_ForScopedApiKey()
        {
            ApiKeyAuthorizationService service = CreateService();

            // Non-root keys should reflect the exact role flags configured in options.
            ApiKeyAccess access = service.Authorize("key-123", "testapp");

            Assert.False(access.IsRoot);
            Assert.False(access.CanAddDatabase);
            Assert.False(access.CanDeleteDatabase);
            Assert.True(access.CanReadDocument);
            Assert.True(access.CanWriteDocument);
            Assert.False(access.CanUpdateDocument);
            Assert.False(access.CanDeleteDocument);
        }

        [Fact]
        public void Authorize_Throws_WhenApiKeyHeaderValueIsMissing()
        {
            ApiKeyAuthorizationService service = CreateService();

            // Empty header value is a malformed request, not a valid auth attempt.
            Assert.Throws<ArgumentException>(() => service.Authorize("", "testapp"));
        }

        private static ApiKeyAuthorizationService CreateService()
        {
            // Shared fixture for these tests:
            // - root key has global access,
            // - key-123 is scoped to testapp with limited write roles.
            ApiKeyAuthorizationOptions options = new ApiKeyAuthorizationOptions
            {
                RootApiKey = "root",
                ApiKeys = new List<ApiKeyEntryOptions>
                {
                    new ApiKeyEntryOptions
                    {
                        Name = "studio-reader-writer",
                        Key = "key-123",
                        Databases = new List<string> { "testapp" },
                        Roles = new Dictionary<string, bool>
                        {
                            ["ADD_DB"] = false,
                            ["DELETE_DB"] = false,
                            ["READ_DOCUMENT"] = true,
                            ["WRITE_DOCUMENT"] = true,
                            ["UPDATE_DOCUMENT"] = false,
                            ["DELETE_DOCUMENT"] = false
                        }
                    }
                }
            };

            return new ApiKeyAuthorizationService(options);
        }
    }
}
