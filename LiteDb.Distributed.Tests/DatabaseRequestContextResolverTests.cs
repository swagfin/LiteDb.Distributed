using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Context;
using LiteDb.Distributed.Infrastructure.Storage;
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

            DatabaseRequestContext context = await scope.Resolver.ResolveAsync(headers);

            Assert.Equal("testapp", context.DatabaseName);
            Assert.Equal("key-123", context.Credential);
        }

        [Fact]
        public async Task ResolveAsync_ThrowsWhenPasswordAndApiKeyMismatch()
        {
            await using TestResolverScope scope = new TestResolverScope();

            HeaderDictionary headers = new HeaderDictionary
            {
                ["Database"] = "testapp",
                ["Password"] = "pass-a",
                ["ApiKey"] = "pass-b"
            };

            await Assert.ThrowsAsync<ArgumentException>(() => scope.Resolver.ResolveAsync(headers));
        }

        private class TestResolverScope : IAsyncDisposable
        {
            private readonly string _rootPath;

            public TestResolverScope()
            {
                _rootPath = Path.Combine(Path.GetTempPath(), "LiteDb.Distributed.Tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_rootPath);

                FileLogicalDatabaseCatalog catalog = new FileLogicalDatabaseCatalog(new ClusterNodeOptions
                {
                    NodeId = "resolver-node",
                    DataDirectory = _rootPath
                });

                Resolver = new DatabaseRequestContextResolver(
                    catalog,
                    NullLogger<DatabaseRequestContextResolver>.Instance);
            }

            public DatabaseRequestContextResolver Resolver { get; }

            public ValueTask DisposeAsync()
            {
                if (Directory.Exists(_rootPath))
                {
                    Directory.Delete(_rootPath, recursive: true);
                }

                return ValueTask.CompletedTask;
            }
        }
    }


}

