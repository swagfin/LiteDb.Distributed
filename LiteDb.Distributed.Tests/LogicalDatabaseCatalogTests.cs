using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Storage;
using LiteDb.Distributed.Tests.TestSupport;

namespace LiteDb.Distributed.Tests
{
    public class LogicalDatabaseCatalogTests
    {
        [Fact]
        public async Task GetOrCreate_NormalizesDatabaseNameToLowercase()
        {
            await using TestCatalogScope scope = new TestCatalogScope();

            // Catalog should normalize user input so downstream file paths remain consistent.
            LogicalDatabaseRegistration created = await scope.Catalog.GetOrCreateAsync("TestApp");
            Assert.Equal("testapp", created.DatabaseName);

            IReadOnlyList<LogicalDatabaseRegistration> all = await scope.Catalog.GetAllAsync();
            Assert.Contains(all, x => x.DatabaseName == "testapp");
        }

        [Fact]
        public async Task ExistsAsync_ReturnsTrueForCreatedDatabase()
        {
            await using TestCatalogScope scope = new TestCatalogScope();

            await scope.Catalog.GetOrCreateAsync("TestApp");
            bool exists = await scope.Catalog.ExistsAsync("testapp");

            Assert.True(exists);
        }

        private class TestCatalogScope : IAsyncDisposable
        {
            private readonly string _rootPath;

            public TestCatalogScope()
            {
                _rootPath = Path.Combine(Path.GetTempPath(), "LiteDb.Distributed.Tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_rootPath);

                // Use isolated temp storage per test scope to avoid cross-run contamination.
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
                TestFileSystem.DeleteDirectoryIfExists(_rootPath);

                return ValueTask.CompletedTask;
            }
        }
    }
}
