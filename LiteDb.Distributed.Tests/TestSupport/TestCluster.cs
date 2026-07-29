using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Infrastructure.Conflict;
using LiteDb.Distributed.Server.Controllers;
using LiteDb.Distributed.Server.Core.Abstractions;
using LiteDb.Distributed.Server.Core.Models;
using LiteDb.Distributed.Server.Core.Queries;
using LiteDb.Distributed.Server.Infrastructure.Replication;
using LiteDb.Distributed.Server.Data;
using LiteDb.Distributed.Tests.TestEntities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace LiteDb.Distributed.Tests.TestSupport
{
    internal static class TestCollections
    {
        public const string Customer = "customers";
    }

    internal static class TestFileSystem
    {
        public static void DeleteDirectoryIfExists(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            IOException? lastIOException = null;

            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (IOException ex)
                {
                    lastIOException = ex;
                    Thread.Sleep(100 * attempt);
                }
            }

            throw lastIOException ?? new IOException($"Could not delete test directory '{path}'.");
        }
    }

    internal class TestCluster : IAsyncDisposable
    {
        private readonly string _rootPath;
        private readonly Dictionary<string, TestNode> _nodes = new(StringComparer.Ordinal);

        public TestCluster()
        {
            _rootPath = Path.Combine(Path.GetTempPath(), "LiteDb.Distributed.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootPath);
        }

        public TestNode AddNode(string nodeId)
        {
            TestNode node = new TestNode(_rootPath, nodeId, ResolveNode);
            _nodes[nodeId] = node;
            return node;
        }

        public async Task ConnectAllAsync()
        {
            List<TestNode> nodes = _nodes.Values.ToList();

            // Build a full-mesh peer view for deterministic test behavior.
            foreach (TestNode source in nodes)
            {
                foreach (TestNode target in nodes)
                {
                    if (source == target)
                    {
                        continue;
                    }

                    await source.AddPeerAsync(target);
                }
            }
        }

        public async Task ReplicateAllAsync(int rounds)
        {
            // Multiple rounds reduce timing flakiness in eventually consistent flows.
            for (int i = 0; i < rounds; i++)
            {
                foreach (TestNode node in _nodes.Values)
                {
                    await node.ReplicateOnceAsync();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (TestNode node in _nodes.Values)
            {
                await node.DisposeAsync();
            }

            TestFileSystem.DeleteDirectoryIfExists(_rootPath);
        }

        private TestNode ResolveNode(string nodeId)
        {
            return _nodes[nodeId];
        }
    }

    internal class TestNode : IAsyncDisposable
    {
        private readonly string _nodeId;
        private readonly IOperationIngestionService _ingestionService;
        private readonly PeerReplicationService _replicationService;
        private readonly ReplicationStatusService _replicationStatusService;
        private readonly QueryController _queryController;
        private readonly DocumentsController _documentsController;

        public TestNode(string rootPath, string nodeId, Func<string, TestNode> nodeResolver)
        {
            _nodeId = nodeId;

            // Each test node gets an isolated single database file under temp storage.
            Store = new LiteDbNodeStore(new LiteDbNodeStoreOptions
            {
                NodeId = nodeId,
                DatabaseName = "testdb",
                DatabasePath = Path.Combine(rootPath, $"{nodeId}.testdb.db")
            });

            IConflictResolver conflictResolver = new NodeConflictPolicyResolver("ApplyIncoming");
            _ingestionService = new OperationIngestionService(Store, conflictResolver, Store, Store, NullLogger<OperationIngestionService>.Instance);
            InMemoryPeerReplicationClient peerClient = new InMemoryPeerReplicationClient(nodeResolver);

            // In-memory client wiring lets tests focus on replication logic without HTTP.
            _replicationService = new PeerReplicationService(
                new ClusterNodeOptions
                {
                    NodeId = nodeId,
                    ReplicationBatchSize = 500
                },
                Store,
                Store,
                Store,
                peerClient,
                _ingestionService,
                NullLogger<PeerReplicationService>.Instance);

            _replicationStatusService = new ReplicationStatusService(
                new ClusterNodeOptions
                {
                    NodeId = nodeId
                },
                new InMemoryLogicalDatabaseCatalog("testdb"),
                new InMemoryLogicalDatabaseStoreProvider(Store),
                NullLogger<ReplicationStatusService>.Instance);

            _queryController = new QueryController(Store, Store, new InMemoryReplicationSignalPublisher(), NullLogger<QueryController>.Instance);
            _documentsController = new DocumentsController(Store, Store, new InMemoryLogicalDatabaseStoreProvider(Store), new InMemoryReplicationSignalPublisher(), NullLogger<DocumentsController>.Instance);
        }

        public LiteDbNodeStore Store { get; }

        public Task AddPeerAsync(TestNode peer)
        {
            return Store.UpsertPeerAsync(new ClusterPeer
            {
                NodeId = peer._nodeId,
                BaseUrl = $"inmemory://{peer._nodeId}",
                IsActive = true,
                UpdatedUtc = DateTime.UtcNow
            });
        }

        public Task ReplicateOnceAsync()
        {
            return _replicationService.ReplicateOnceAsync();
        }

        public Task<ReplicationStatusSnapshot> GetReplicationStatusAsync()
        {
            return _replicationStatusService.GetStatusAsync();
        }

        public Task UpsertCustomerAsync(string customerId, string customerName)
        {
            return Store.UpsertAsync(
                TestCollections.Customer,
                customerId,
                new Customer
                {
                    Id = customerId,
                    Name = customerName,
                    Email = $"{customerId}@example.com",
                    UpdatedUtc = DateTime.UtcNow
                });
        }

        public Task DeleteCustomerAsync(string customerId)
        {
            return Store.DeleteAsync(TestCollections.Customer, customerId);
        }

        public Task<Dictionary<string, object?>?> GetCustomerAsync(string customerId)
        {
            return Store.GetByIdAsync<Dictionary<string, object?>>(TestCollections.Customer, customerId);
        }

        public async Task<QueryResponse> ExecuteQueryAsync(string query, int take = 200)
        {
            // Tests assert on controller-level responses to exercise query safety behavior.
            IActionResult result = await _queryController.ExecuteAsync(new QueryRequest
            {
                Query = query,
                Take = take
            }, false, CancellationToken.None);

            if (result is OkObjectResult okResult && okResult.Value is QueryResponse response)
            {
                return response;
            }

            if (result is ObjectResult objectResult)
            {
                string responseJson = JsonSerializer.Serialize(objectResult.Value);
                throw new InvalidOperationException($"Query failed with status code {objectResult.StatusCode}. Response={responseJson}");
            }

            throw new InvalidOperationException("Query failed with unexpected result type.");
        }

        public async Task PutCustomerViaControllerAsync(string routeId, string payloadJson)
        {
            using JsonDocument payload = JsonDocument.Parse(payloadJson);
            IActionResult result = await _documentsController.PutAsync(TestCollections.Customer, routeId, payload.RootElement.Clone(), null, CancellationToken.None);

            if (result is OkObjectResult)
            {
                return;
            }

            if (result is ObjectResult objectResult)
            {
                throw new InvalidOperationException($"PUT failed with status code {objectResult.StatusCode}.");
            }

            throw new InvalidOperationException("PUT failed with unexpected result type.");
        }

        public async Task<ReplicationPushResponse> ReceivePushAsync(ReplicationPushRequest request, CancellationToken cancellationToken)
        {
            OperationIngestionResult result = await _ingestionService.IngestAsync(_nodeId, request.Operations, cancellationToken);
            return new ReplicationPushResponse
            {
                ProcessedCount = result.ProcessedCount,
                AcceptedCount = result.AcceptedCount,
                LastProcessedLogSequence = result.LastProcessedLogSequence
            };
        }

        public async Task<ReplicationPullResponse> ReceivePullAsync(ReplicationPullRequest request, CancellationToken cancellationToken)
        {
            IReadOnlyList<OperationRecord> operations = await Store.GetOperationsAfterLogSequenceAsync(request.AfterLogSequence, request.BatchSize, cancellationToken);
            return new ReplicationPullResponse
            {
                Operations = operations
            };
        }

        public ValueTask DisposeAsync()
        {
            Store.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    internal class InMemoryPeerReplicationClient : IPeerReplicationClient
    {
        private readonly Func<string, TestNode> _nodeResolver;

        public InMemoryPeerReplicationClient(Func<string, TestNode> nodeResolver)
        {
            _nodeResolver = nodeResolver;
        }

        public Task<ReplicationPushResponse> PushAsync(ClusterPeer peer, ReplicationPushRequest request, CancellationToken cancellationToken = default)
        {
            return _nodeResolver(peer.NodeId).ReceivePushAsync(request, cancellationToken);
        }

        public Task<ReplicationPullResponse> PullAsync(ClusterPeer peer, ReplicationPullRequest request, CancellationToken cancellationToken = default)
        {
            return _nodeResolver(peer.NodeId).ReceivePullAsync(request, cancellationToken);
        }
    }

    internal class InMemoryReplicationSignalPublisher : IReplicationSignalPublisher
    {
        public void NotifyLocalChange(string reason)
        {
        }
    }

    internal class PartialPushPeerReplicationClient : IPeerReplicationClient
    {
        private readonly long _lastProcessedLogSequence;

        public PartialPushPeerReplicationClient(long lastProcessedLogSequence)
        {
            _lastProcessedLogSequence = lastProcessedLogSequence;
        }

        public Task<ReplicationPushResponse> PushAsync(ClusterPeer peer, ReplicationPushRequest request, CancellationToken cancellationToken = default)
        {
            int processedCount = request.Operations.Count(x => x.LogSequence <= _lastProcessedLogSequence);
            return Task.FromResult(new ReplicationPushResponse
            {
                ProcessedCount = processedCount,
                AcceptedCount = processedCount,
                LastProcessedLogSequence = _lastProcessedLogSequence
            });
        }

        public Task<ReplicationPullResponse> PullAsync(ClusterPeer peer, ReplicationPullRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ReplicationPullResponse { Operations = Array.Empty<OperationRecord>() });
        }
    }

    internal class InMemoryLogicalDatabaseStoreProvider : ILogicalDatabaseStoreProvider
    {
        private readonly LiteDbNodeStore _store;

        public InMemoryLogicalDatabaseStoreProvider(LiteDbNodeStore store)
        {
            _store = store;
        }

        public Task<LiteDbNodeStore> GetCurrentStoreAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_store);
        }

        public Task<LiteDbNodeStore> GetStoreAsync(string databaseName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_store);
        }

        public void Dispose()
        {
        }
    }

    internal class InMemoryLogicalDatabaseCatalog : ILogicalDatabaseCatalog
    {
        private readonly string _databaseName;

        public InMemoryLogicalDatabaseCatalog(string databaseName)
        {
            _databaseName = databaseName;
        }

        public Task<LogicalDatabaseRegistration> GetOrCreateAsync(string databaseName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CreateRegistration());
        }

        public Task<bool> ExistsAsync(string databaseName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Equals(databaseName, _databaseName, StringComparison.Ordinal));
        }

        public Task<IReadOnlyList<LogicalDatabaseRegistration>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<LogicalDatabaseRegistration> registrations = new[] { CreateRegistration() };
            return Task.FromResult(registrations);
        }

        private LogicalDatabaseRegistration CreateRegistration()
        {
            DateTime now = DateTime.UtcNow;
            return new LogicalDatabaseRegistration
            {
                DatabaseName = _databaseName,
                CreatedUtc = now,
                UpdatedUtc = now
            };
        }
    }
}
