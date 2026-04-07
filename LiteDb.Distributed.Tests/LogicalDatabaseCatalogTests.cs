using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Context;
using LiteDb.Distributed.Infrastructure.Storage;

namespace LiteDb.Distributed.Tests
{
    public class LogicalDatabaseCatalogTests
    {
        [Fact]
        public async Task GetOrCreate_NormalizesDatabaseNameToLowercase()
        {
            await using TestCatalogScope scope = new TestCatalogScope();

            LogicalDatabaseRegistration created = await scope.Catalog.GetOrCreateAsync("TestApp", "secret-1");
            Assert.Equal("testapp", created.DatabaseName);

            IReadOnlyList<LogicalDatabaseRegistration> all = await scope.Catalog.GetAllAsync();
            Assert.Contains(all, x => x.DatabaseName == "testapp");
        }

        [Fact]
        public async Task GetOrCreate_ThrowsWhenCredentialDiffersForExistingDatabase()
        {
            await using TestCatalogScope scope = new TestCatalogScope();

            await scope.Catalog.GetOrCreateAsync("TestApp", "secret-1");

            await Assert.ThrowsAsync<DatabaseAuthenticationException>(async () =>
            {
                await scope.Catalog.GetOrCreateAsync("testapp", "secret-2");
            });
        }

        private class TestCatalogScope : IAsyncDisposable
        {
            private readonly string _rootPath;

            public TestCatalogScope()
            {
                _rootPath = Path.Combine(Path.GetTempPath(), "LiteDb.Distributed.Tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_rootPath);

                Catalog = new FileLogicalDatabaseCatalog(new ClusterNodeOptions
                {
                    NodeId = "node-test",
                    DataDirectory = _rootPath,
                    ReplicationBatchSize = 100
                });
            }

            public FileLogicalDatabaseCatalog Catalog { get; }

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

