using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Core.Context;
using LiteDb.Distributed.Server.Data;
using LiteDb.Distributed.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteDb.Distributed.Tests
{
    public class DatabaseRequestContextResolverTests
    {
        [Fact]
        public async Task ResolveAsync_UsesApiKeyAndNormalizesDatabaseName()
        {
            await using TestResolverScope scope = new TestResolverScope();

            HeaderDictionary headers = new HeaderDictionary
            {
                ["Database"] = "TestApp",
                ["ApiKey"] = "key-123"
            };

            // Resolver normalizes tenant/database identity before setting request context.
            DatabaseRequestContext context = await scope.Resolver.ResolveAsync(headers);

            Assert.Equal("testapp", context.DatabaseName);
            Assert.Equal("key-123", context.ApiKey);
            Assert.True(context.CanReadDocument);
        }

        [Fact]
        public async Task ResolveAsync_ThrowsWhenApiKeyHeaderMissing()
        {
            await using TestResolverScope scope = new TestResolverScope();

            HeaderDictionary headers = new HeaderDictionary
            {
                ["Database"] = "testapp"
            };

            // Missing ApiKey should fail fast as a bad request.
            await Assert.ThrowsAsync<ArgumentException>(() => scope.Resolver.ResolveAsync(headers));
        }

        [Fact]
        public async Task ResolveAsync_ThrowsWhenDatabaseMissingAndApiKeyCannotAddDatabase()
        {
            await using TestResolverScope scope = new TestResolverScope(canAddDatabase: false);

            HeaderDictionary headers = new HeaderDictionary
            {
                ["Database"] = "newdb",
                ["ApiKey"] = "key-123"
            };

            // Key can authenticate, but lacks permission to create a missing logical DB.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scope.Resolver.ResolveAsync(headers));
        }

        [Fact]
        public async Task ResolveAsync_AllowsDatabaseCreationWhenApiKeyCanAddDatabase()
        {
            await using TestResolverScope scope = new TestResolverScope(canAddDatabase: true, allowedDatabases: new List<string> { "*" });

            HeaderDictionary headers = new HeaderDictionary
            {
                ["Database"] = "newdb",
                ["ApiKey"] = "key-123"
            };

            // Wildcard DB scope + ADD_DB role allows first-touch database creation.
            DatabaseRequestContext context = await scope.Resolver.ResolveAsync(headers);

            Assert.Equal("newdb", context.DatabaseName);
            Assert.True(context.CanAddDatabase);
        }

        private class TestResolverScope : IAsyncDisposable
        {
            private readonly string _rootPath;

            public TestResolverScope(bool canAddDatabase = true, List<string>? allowedDatabases = null)
            {
                _rootPath = Path.Combine(Path.GetTempPath(), "LiteDb.Distributed.Tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_rootPath);

                // Catalog persists per-test in a temp folder to avoid cross-test contamination.
                FileLogicalDatabaseCatalog catalog = new FileLogicalDatabaseCatalog(new ClusterNodeOptions
                {
                    NodeId = "resolver-node",
                    DataDirectory = _rootPath
                });

                ApiKeyAuthorizationOptions options = new ApiKeyAuthorizationOptions
                {
                    RootApiKey = "root",
                    ApiKeys = new List<ApiKeyEntryOptions>
                    {
                        new ApiKeyEntryOptions
                        {
                            Name = "test-key",
                            Key = "key-123",
                            Databases = allowedDatabases ?? new List<string> { "testapp" },
                            Roles = new Dictionary<string, bool>
                            {
                                ["ADD_DB"] = canAddDatabase,
                                ["READ_DOCUMENT"] = true
                            }
                        }
                    }
                };

                ApiKeyAuthorizationService authService = new ApiKeyAuthorizationService(options);
                Resolver = new DatabaseRequestContextResolver(catalog, authService, NullLogger<DatabaseRequestContextResolver>.Instance);
            }

            public DatabaseRequestContextResolver Resolver { get; }

            public ValueTask DisposeAsync()
            {
                TestFileSystem.DeleteDirectoryIfExists(_rootPath);

                return ValueTask.CompletedTask;
            }
        }
    }
}
