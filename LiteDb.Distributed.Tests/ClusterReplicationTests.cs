using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Conflict;
using LiteDb.Distributed.Infrastructure.Replication;
using LiteDb.Distributed.Infrastructure.Storage;
using LiteDb.Distributed.Server.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteDb.Distributed.Tests
{
    public class ClusterReplicationTests
    {
        [Fact]
        public async Task WriteOnOneNode_ReplicatesToOtherNodes()
        {
            await using TestCluster cluster = new TestCluster();
            TestNode nodeA = cluster.AddNode("node-a");
            TestNode nodeB = cluster.AddNode("node-b");
            TestNode nodeC = cluster.AddNode("node-c");

            await cluster.ConnectAllAsync();

            await nodeA.UpsertCustomerAsync("cust-001", "Acme One");
            await cluster.ReplicateAllAsync(rounds: 3);

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
            private const string CustomerCollection = "customers";
            private readonly string _nodeId;
            private readonly IOperationIngestionService _ingestionService;
            private readonly PeerReplicationService _replicationService;
            private readonly QueryController _queryController;

            public TestNode(string rootPath, string nodeId, Func<string, TestNode> nodeResolver)
            {
                _nodeId = nodeId;

                Store = new LiteDbNodeStore(new LiteDbNodeStoreOptions
                {
                    NodeId = nodeId,
                    DatabaseName = "testdb",
                    BusinessDatabasePath = Path.Combine(rootPath, $"{nodeId}.testdb.db"),
                    MetadataDatabasePath = Path.Combine(rootPath, $"{nodeId}.testdb.db.metadata")
                });

                IConflictResolver conflictResolver = new CriticalCollectionConflictResolver(new LastWriteWinsConflictResolver(), Array.Empty<string>());

                _ingestionService = new OperationIngestionService(Store, conflictResolver, Store, Store, NullLogger<OperationIngestionService>.Instance);

                InMemoryPeerReplicationClient peerClient = new InMemoryPeerReplicationClient(nodeResolver);

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
                IActionResult result = await _queryController.ExecuteAsync(new QueryController.QueryRequest
                {
                    Query = query,
                    Take = take
                }, CancellationToken.None);

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

            public async Task<ReplicationPushResponse> ReceivePushAsync(ReplicationPushRequest request, CancellationToken cancellationToken)
            {
                OperationIngestionResult result = await _ingestionService.IngestAsync(_nodeId, request.Operations, cancellationToken);
                return new ReplicationPushResponse
                {
                    AcceptedCount = result.AcceptedCount
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
    }

}
