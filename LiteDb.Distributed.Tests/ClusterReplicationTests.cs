using LiteDb.Distributed.Server.Domain.Abstractions;
using LiteDb.Distributed.Server.Domain.Models;
using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Conflict;
using LiteDb.Distributed.Server.Replication;
using LiteDb.Distributed.Server.Storage;
using LiteDb.Distributed.Server.Controllers;
using LiteDb.Distributed.Tests.TestEntities;
using LiteDB;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace LiteDb.Distributed.Tests
{
    public class ClusterReplicationTests
    {
        private const string CustomerCollection = "customers";

        [Fact]
        public async Task WriteOnOneNode_ReplicatesToOtherNodes()
        {
            // Three-node topology gives us a simple way to verify fan-out replication.
            await using TestCluster cluster = new TestCluster();
            TestNode nodeA = cluster.AddNode("node-a");
            TestNode nodeB = cluster.AddNode("node-b");
            TestNode nodeC = cluster.AddNode("node-c");

            await cluster.ConnectAllAsync();

            await nodeA.UpsertCustomerAsync("cust-001", "Acme One");
            await cluster.ReplicateAllAsync(rounds: 3);

            // We validate materialized business state, not just operation-log counts.
            Dictionary<string, object?>? bCustomer = await nodeB.GetCustomerAsync("cust-001");
            Dictionary<string, object?>? cCustomer = await nodeC.GetCustomerAsync("cust-001");

            Assert.NotNull(bCustomer);
            Assert.NotNull(cCustomer);
            Assert.Equal("Acme One", bCustomer!["Name"]?.ToString());
            Assert.Equal("Acme One", cCustomer!["Name"]?.ToString());
        }

        [Fact]
        public async Task NewNode_CatchesUpFromExistingNode()
        {
            await using TestCluster cluster = new TestCluster();
            TestNode nodeA = cluster.AddNode("node-a");
            TestNode nodeB = cluster.AddNode("node-b");

            await cluster.ConnectAllAsync();

            await nodeA.UpsertCustomerAsync("cust-join-001", "Join Target");
            await cluster.ReplicateAllAsync(rounds: 2);

            // Node C joins later and should catch up from peers via pull/push cycles.
            TestNode nodeC = cluster.AddNode("node-c");
            await nodeC.AddPeerAsync(nodeA);
            await nodeC.AddPeerAsync(nodeB);

            await nodeC.ReplicateOnceAsync();
            await nodeC.ReplicateOnceAsync();

            Dictionary<string, object?>? cCustomer = await nodeC.GetCustomerAsync("cust-join-001");
            Assert.NotNull(cCustomer);
            Assert.Equal("Join Target", cCustomer!["Name"]?.ToString());
        }

        [Fact]
        public async Task DeleteReplicatesAsTombstoneAcrossNodes()
        {
            await using TestCluster cluster = new TestCluster();
            TestNode nodeA = cluster.AddNode("node-a");
            TestNode nodeB = cluster.AddNode("node-b");

            await cluster.ConnectAllAsync();

            await nodeA.UpsertCustomerAsync("cust-del-001", "To Delete");
            await cluster.ReplicateAllAsync(rounds: 2);

            await nodeB.DeleteCustomerAsync("cust-del-001");
            await cluster.ReplicateAllAsync(rounds: 3);

            Dictionary<string, object?>? aCustomer = await nodeA.GetCustomerAsync("cust-del-001");
            Dictionary<string, object?>? bCustomer = await nodeB.GetCustomerAsync("cust-del-001");

            Assert.Null(aCustomer);
            Assert.Null(bCustomer);
        }

        [Fact]
        public async Task LocalWrites_AppendImmutableOperationEntries()
        {
            await using TestCluster cluster = new TestCluster();
            TestNode nodeA = cluster.AddNode("node-a");

            await nodeA.UpsertCustomerAsync("cust-op-001", "Op One");
            await nodeA.UpsertCustomerAsync("cust-op-002", "Op Two");
            await nodeA.DeleteCustomerAsync("cust-op-001");

            // Log sequence must stay monotonic and unique to support deterministic replay.
            IReadOnlyList<OperationRecord> operations = await nodeA.Store.GetOperationsAfterLogSequenceAsync(0, 100);
            Assert.Equal(3, operations.Count);
            Assert.Equal(new[] { 1L, 2L, 3L }, operations.Select(x => x.LogSequence));
            Assert.Equal(3, operations.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public async Task BulkUpdateQuery_PropagatesAcrossNodes()
        {
            await using TestCluster cluster = new TestCluster();
            TestNode nodeA = cluster.AddNode("node-a");
            TestNode nodeB = cluster.AddNode("node-b");
            TestNode nodeC = cluster.AddNode("node-c");

            await cluster.ConnectAllAsync();

            await nodeA.UpsertCustomerAsync("cust-bulk-upd-001", "Bulk One");
            await nodeA.UpsertCustomerAsync("cust-bulk-upd-002", "Bulk Two");
            await nodeA.UpsertCustomerAsync("cust-bulk-upd-003", "Bulk Three");
            await cluster.ReplicateAllAsync(rounds: 3);

            QueryController.QueryResponse response = await nodeA.ExecuteQueryAsync("UPDATE customers SET {\"Tier\":\"vip\"} WHERE $_id = 'cust-bulk-upd-001' OR $_id = 'cust-bulk-upd-002'");
            await cluster.ReplicateAllAsync(rounds: 3);

            // Safe query mode reports matched rows and applied writes separately.
            Assert.Equal(2, response.MatchedCount);
            Assert.Equal(2, response.AppliedCount);

            Dictionary<string, object?>? b1 = await nodeB.GetCustomerAsync("cust-bulk-upd-001");
            Dictionary<string, object?>? b2 = await nodeB.GetCustomerAsync("cust-bulk-upd-002");
            Dictionary<string, object?>? c1 = await nodeC.GetCustomerAsync("cust-bulk-upd-001");
            Dictionary<string, object?>? c2 = await nodeC.GetCustomerAsync("cust-bulk-upd-002");
            Dictionary<string, object?>? b3 = await nodeB.GetCustomerAsync("cust-bulk-upd-003");
            Dictionary<string, object?>? c3 = await nodeC.GetCustomerAsync("cust-bulk-upd-003");

            Assert.Equal("vip", b1!["Tier"]?.ToString());
            Assert.Equal("vip", b2!["Tier"]?.ToString());
            Assert.Equal("vip", c1!["Tier"]?.ToString());
            Assert.Equal("vip", c2!["Tier"]?.ToString());
            Assert.False(b3!.ContainsKey("Tier"));
            Assert.False(c3!.ContainsKey("Tier"));
        }

        [Fact]
        public async Task BulkDeleteQuery_PropagatesAcrossNodes()
        {
            await using TestCluster cluster = new TestCluster();
            TestNode nodeA = cluster.AddNode("node-a");
            TestNode nodeB = cluster.AddNode("node-b");
            TestNode nodeC = cluster.AddNode("node-c");

            await cluster.ConnectAllAsync();

            await nodeA.UpsertCustomerAsync("cust-bulk-del-001", "Delete One");
            await nodeA.UpsertCustomerAsync("cust-bulk-del-002", "Delete Two");
            await nodeA.UpsertCustomerAsync("cust-bulk-del-003", "Delete Three");
            await cluster.ReplicateAllAsync(rounds: 3);

            QueryController.QueryResponse response = await nodeA.ExecuteQueryAsync("DELETE FROM customers WHERE $_id = 'cust-bulk-del-001' OR $_id = 'cust-bulk-del-002'");
            await cluster.ReplicateAllAsync(rounds: 3);

            // Two targeted documents should be removed everywhere, while unrelated rows remain.
            Assert.Equal(2, response.MatchedCount);
            Assert.Equal(2, response.AppliedCount);

            Dictionary<string, object?>? b1 = await nodeB.GetCustomerAsync("cust-bulk-del-001");
            Dictionary<string, object?>? b2 = await nodeB.GetCustomerAsync("cust-bulk-del-002");
            Dictionary<string, object?>? c1 = await nodeC.GetCustomerAsync("cust-bulk-del-001");
            Dictionary<string, object?>? c2 = await nodeC.GetCustomerAsync("cust-bulk-del-002");
            Dictionary<string, object?>? b3 = await nodeB.GetCustomerAsync("cust-bulk-del-003");
            Dictionary<string, object?>? c3 = await nodeC.GetCustomerAsync("cust-bulk-del-003");

            Assert.Null(b1);
            Assert.Null(b2);
            Assert.Null(c1);
            Assert.Null(c2);
            Assert.NotNull(b3);
            Assert.NotNull(c3);
        }

        [Fact]
        public async Task PutRouteId_OverridesBodyId()
        {
            await using TestCluster cluster = new TestCluster();
            TestNode nodeA = cluster.AddNode("node-a");

            await nodeA.PutCustomerViaControllerAsync("cust-route-001", "{\"Id\":\"\",\"Name\":\"Route Wins\",\"Email\":\"route@example.com\"}");

            Dictionary<string, object?>? stored = await nodeA.GetCustomerAsync("cust-route-001");
            Assert.NotNull(stored);
            Assert.Equal("cust-route-001", stored!["Id"]?.ToString());
        }

        [Fact]
        public async Task PushCheckpoint_AdvancesOnlyToPeerProcessedLogSequence()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), "LiteDb.Distributed.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);

            try
            {
                using LiteDbNodeStore store = new LiteDbNodeStore(new LiteDbNodeStoreOptions
                {
                    NodeId = "node-a",
                    DatabaseName = "testdb",
                    DatabasePath = Path.Combine(rootPath, "node-a.testdb.db")
                });

                await store.UpsertPeerAsync(new ClusterPeer
                {
                    NodeId = "node-b",
                    BaseUrl = "inmemory://node-b",
                    IsActive = true,
                    UpdatedUtc = DateTime.UtcNow
                });

                await store.UpsertAsync(CustomerCollection, "cust-progress-001", new Customer { Id = "cust-progress-001", Name = "One", Email = "one@example.com", UpdatedUtc = DateTime.UtcNow });
                await store.UpsertAsync(CustomerCollection, "cust-progress-002", new Customer { Id = "cust-progress-002", Name = "Two", Email = "two@example.com", UpdatedUtc = DateTime.UtcNow });

                PartialPushPeerReplicationClient peerClient = new PartialPushPeerReplicationClient(lastProcessedLogSequence: 1);
                PeerReplicationService replicationService = new PeerReplicationService(
                    new ClusterNodeOptions
                    {
                        NodeId = "node-a",
                        ReplicationBatchSize = 500
                    },
                    store,
                    store,
                    store,
                    peerClient,
                    new OperationIngestionService(store, new NodeConflictPolicyResolver("ApplyIncoming"), store, store, NullLogger<OperationIngestionService>.Instance),
                    NullLogger<PeerReplicationService>.Instance);

                await replicationService.ReplicateOnceAsync();

                PeerCheckpointRecord checkpoint = await store.GetOrCreatePeerCheckpointAsync("node-a", "node-b");
                IReadOnlyList<OperationRecord> remaining = await store.GetOperationsAfterLogSequenceAsync(checkpoint.LastPushedLocalLogSequence, 10);

                Assert.Equal(1, checkpoint.LastPushedLocalLogSequence);
                Assert.Single(remaining);
                Assert.Equal("cust-progress-002", remaining[0].EntityId);
            }
            finally
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
        }

        [Fact]
        public async Task IngestAsync_CountsDuplicateOperationAsProcessedProgress()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), "LiteDb.Distributed.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);

            try
            {
                using LiteDbNodeStore sourceStore = new LiteDbNodeStore(new LiteDbNodeStoreOptions
                {
                    NodeId = "node-a",
                    DatabaseName = "testdb",
                    DatabasePath = Path.Combine(rootPath, "node-a.testdb.db")
                });
                using LiteDbNodeStore targetStore = new LiteDbNodeStore(new LiteDbNodeStoreOptions
                {
                    NodeId = "node-b",
                    DatabaseName = "testdb",
                    DatabasePath = Path.Combine(rootPath, "node-b.testdb.db")
                });

                await sourceStore.UpsertAsync(CustomerCollection, "cust-dup-001", new Customer { Id = "cust-dup-001", Name = "Dup", Email = "dup@example.com", UpdatedUtc = DateTime.UtcNow });
                IReadOnlyList<OperationRecord> operations = await sourceStore.GetOperationsAfterLogSequenceAsync(0, 10);

                OperationIngestionService ingestionService = new OperationIngestionService(targetStore, new NodeConflictPolicyResolver("ApplyIncoming"), targetStore, targetStore, NullLogger<OperationIngestionService>.Instance);
                OperationIngestionResult first = await ingestionService.IngestAsync("node-b", operations);
                OperationIngestionResult second = await ingestionService.IngestAsync("node-b", operations);

                Assert.Equal(1, first.ProcessedCount);
                Assert.Equal(1, first.AcceptedCount);
                Assert.Equal(1, first.LastProcessedLogSequence);
                Assert.Equal(1, second.ProcessedCount);
                Assert.Equal(0, second.AcceptedCount);
                Assert.Equal(1, second.LastProcessedLogSequence);
            }
            finally
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
        }

        [Fact]
        public async Task LocalWrite_StoresBusinessDocumentAndOperationLogInSameDatabaseFile()
        {
            string rootPath = Path.Combine(Path.GetTempPath(), "LiteDb.Distributed.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            string databasePath = Path.Combine(rootPath, "node-a.testdb.db");

            try
            {
                using (LiteDbNodeStore store = new LiteDbNodeStore(new LiteDbNodeStoreOptions
                {
                    NodeId = "node-a",
                    DatabaseName = "testdb",
                    DatabasePath = databasePath
                }))
                {
                    await store.UpsertAsync(CustomerCollection, "cust-single-file-001", new Customer { Id = "cust-single-file-001", Name = "Single File", Email = "single@example.com", UpdatedUtc = DateTime.UtcNow });
                }

                using LiteDatabase database = new LiteDatabase(databasePath);
                BsonDocument? customer = database.GetCollection<BsonDocument>(CustomerCollection).FindById("cust-single-file-001");
                BsonDocument? operation = database.GetCollection("_sys_operations").FindAll().FirstOrDefault();

                Assert.NotNull(customer);
                Assert.NotNull(operation);
                Assert.False(File.Exists(Path.Combine(rootPath, "node-a.testdb.db.metadata")));
            }
            finally
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
        }

        private class TestCluster : IAsyncDisposable
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
                foreach (TestNode? source in nodes)
                {
                    foreach (TestNode? target in nodes)
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

                if (Directory.Exists(_rootPath))
                {
                    Directory.Delete(_rootPath, recursive: true);
                }
            }

            private TestNode ResolveNode(string nodeId)
            {
                return _nodes[nodeId];
            }
        }

        private class TestNode : IAsyncDisposable
        {
            private readonly string _nodeId;
            private readonly IOperationIngestionService _ingestionService;
            private readonly PeerReplicationService _replicationService;
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

            public Task UpsertCustomerAsync(string customerId, string customerName)
            {
                return Store.UpsertAsync(
                    CustomerCollection,
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
                return Store.DeleteAsync(CustomerCollection, customerId);
            }

            public Task<Dictionary<string, object?>?> GetCustomerAsync(string customerId)
            {
                return Store.GetByIdAsync<Dictionary<string, object?>>(CustomerCollection, customerId);
            }

            public async Task<QueryController.QueryResponse> ExecuteQueryAsync(string query, int take = 200)
            {
                // Tests assert on controller-level responses to exercise query safety behavior.
                IActionResult result = await _queryController.ExecuteAsync(new QueryController.QueryRequest
                {
                    Query = query,
                    Take = take
                }, false, CancellationToken.None);

                if (result is OkObjectResult okResult && okResult.Value is QueryController.QueryResponse response)
                {
                    return response;
                }

                if (result is ObjectResult objectResult)
                {
                    throw new InvalidOperationException($"Query failed with status code {objectResult.StatusCode}.");
                }

                throw new InvalidOperationException("Query failed with unexpected result type.");
            }

            public async Task PutCustomerViaControllerAsync(string routeId, string payloadJson)
            {
                using JsonDocument payload = JsonDocument.Parse(payloadJson);
                IActionResult result = await _documentsController.PutAsync(CustomerCollection, routeId, payload.RootElement.Clone(), null, CancellationToken.None);

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

        private class InMemoryPeerReplicationClient : IPeerReplicationClient
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

        private class InMemoryReplicationSignalPublisher : IReplicationSignalPublisher
        {
            public void NotifyLocalChange(string reason)
            {
            }
        }

        private class PartialPushPeerReplicationClient : IPeerReplicationClient
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

        private class InMemoryLogicalDatabaseStoreProvider : ILogicalDatabaseStoreProvider
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
    }

}
