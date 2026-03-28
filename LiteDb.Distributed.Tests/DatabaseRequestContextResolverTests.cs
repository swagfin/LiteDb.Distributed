using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Context;
using LiteDb.Distributed.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;

namespace LiteDb.Distributed.Tests;

public sealed class DatabaseRequestContextResolverTests
{
    [Fact]
    public async Task ResolveAsync_UsesApiKeyAndNormalizesDatabaseName()
    {
        await using var scope = new TestResolverScope();

        var headers = new HeaderDictionary
        {
            ["Database"] = "TestApp",
            ["ApiKey"] = "key-123"
        };

        var context = await scope.Resolver.ResolveAsync(headers);

        Assert.Equal("testapp", context.DatabaseName);
        Assert.Equal("key-123", context.Credential);
    }

    [Fact]
    public async Task ResolveAsync_ThrowsWhenPasswordAndApiKeyMismatch()
    {
        await using var scope = new TestResolverScope();

        var headers = new HeaderDictionary
        {
            ["Database"] = "testapp",
            ["Password"] = "pass-a",
            ["ApiKey"] = "pass-b"
        };

        await Assert.ThrowsAsync<ArgumentException>(() => scope.Resolver.ResolveAsync(headers));
    }

    private sealed class TestResolverScope : IAsyncDisposable
    {
        private readonly string _rootPath;

        public TestResolverScope()
        {
            _rootPath = Path.Combine(Path.GetTempPath(), "LiteDb.Distributed.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootPath);

            var catalog = new FileLogicalDatabaseCatalog(new ClusterNodeOptions
            {
                NodeId = "resolver-node",
                DataDirectory = _rootPath
            });

            Resolver = new DatabaseRequestContextResolver(catalog);
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
