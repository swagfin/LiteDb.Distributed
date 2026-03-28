using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Collections;
using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Core.SampleEntities;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Conflict;
using LiteDb.Distributed.Infrastructure.Replication;
using LiteDb.Distributed.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteDb.Distributed.Tests;

public sealed class ClusterReplicationTests
{
    [Fact]
    public async Task WriteOnOneNode_ReplicatesToOtherNodes()
    {
        await using var cluster = new TestCluster();
        var nodeA = cluster.AddNode("node-a");
        var nodeB = cluster.AddNode("node-b");
        var nodeC = cluster.AddNode("node-c");

        await cluster.ConnectAllAsync();

        await nodeA.UpsertCustomerAsync("cust-001", "Acme One");
        await cluster.ReplicateAllAsync(rounds: 3);

        var bCustomer = await nodeB.GetCustomerAsync("cust-001");
        var cCustomer = await nodeC.GetCustomerAsync("cust-001");

        Assert.NotNull(bCustomer);
        Assert.NotNull(cCustomer);
        Assert.Equal("Acme One", bCustomer!["Name"]?.ToString());
        Assert.Equal("Acme One", cCustomer!["Name"]?.ToString());
    }

    [Fact]
    public async Task NewNode_CatchesUpFromExistingNode()
    {
        await using var cluster = new TestCluster();
        var nodeA = cluster.AddNode("node-a");
        var nodeB = cluster.AddNode("node-b");

        await cluster.ConnectAllAsync();

        await nodeA.UpsertCustomerAsync("cust-join-001", "Join Target");
        await cluster.ReplicateAllAsync(rounds: 2);

        var nodeC = cluster.AddNode("node-c");
        await nodeC.AddPeerAsync(nodeA);
        await nodeC.AddPeerAsync(nodeB);

        await nodeC.ReplicateOnceAsync();
        await nodeC.ReplicateOnceAsync();

        var cCustomer = await nodeC.GetCustomerAsync("cust-join-001");
        Assert.NotNull(cCustomer);
        Assert.Equal("Join Target", cCustomer!["Name"]?.ToString());
    }

    [Fact]
    public async Task DeleteReplicatesAsTombstoneAcrossNodes()
    {
        await using var cluster = new TestCluster();
        var nodeA = cluster.AddNode("node-a");
        var nodeB = cluster.AddNode("node-b");

        await cluster.ConnectAllAsync();

        await nodeA.UpsertCustomerAsync("cust-del-001", "To Delete");
        await cluster.ReplicateAllAsync(rounds: 2);

        await nodeB.DeleteCustomerAsync("cust-del-001");
        await cluster.ReplicateAllAsync(rounds: 3);

        var aCustomer = await nodeA.GetCustomerAsync("cust-del-001");
        var bCustomer = await nodeB.GetCustomerAsync("cust-del-001");

        Assert.Null(aCustomer);
        Assert.Null(bCustomer);
    }

    [Fact]
    public async Task LocalWrites_AppendImmutableOperationEntries()
    {
        await using var cluster = new TestCluster();
        var nodeA = cluster.AddNode("node-a");

        await nodeA.UpsertCustomerAsync("cust-op-001", "Op One");
        await nodeA.UpsertCustomerAsync("cust-op-002", "Op Two");
        await nodeA.DeleteCustomerAsync("cust-op-001");

        var operations = await nodeA.Store.GetOperationsAfterLogSequenceAsync(0, 100);
        Assert.Equal(3, operations.Count);
        Assert.Equal(new[] { 1L, 2L, 3L }, operations.Select(x => x.LogSequence));
        Assert.Equal(3, operations.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count());
    }

    private sealed class TestCluster : IAsyncDisposable
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
            var node = new TestNode(_rootPath, nodeId, ResolveNode);
            _nodes[nodeId] = node;
            return node;
        }

        public async Task ConnectAllAsync()
        {
            var nodes = _nodes.Values.ToList();

            foreach (var source in nodes)
            {
                foreach (var target in nodes)
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
            for (var i = 0; i < rounds; i++)
            {
                foreach (var node in _nodes.Values)
                {
                    await node.ReplicateOnceAsync();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var node in _nodes.Values)
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

    private sealed class TestNode : IAsyncDisposable
    {
        private readonly string _nodeId;
        private readonly IOperationIngestionService _ingestionService;
        private readonly PeerReplicationService _replicationService;

        public TestNode(
            string rootPath,
            string nodeId,
            Func<string, TestNode> nodeResolver)
        {
            _nodeId = nodeId;

            Store = new LiteDbNodeStore(new LiteDbNodeStoreOptions
            {
                NodeId = nodeId,
                DatabaseName = "testdb",
                BusinessDatabasePath = Path.Combine(rootPath, $"{nodeId}.testdb.db"),
                MetadataDatabasePath = Path.Combine(rootPath, $"{nodeId}.testdb.db.metadata")
            });

            IConflictResolver conflictResolver = new CriticalCollectionConflictResolver(
                new LastWriteWinsConflictResolver(),
                Array.Empty<string>());

            _ingestionService = new OperationIngestionService(
                Store,
                conflictResolver,
                Store,
                Store,
                NullLogger<OperationIngestionService>.Instance);

            var peerClient = new InMemoryPeerReplicationClient(nodeResolver);

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
                BusinessCollections.Customers,
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
            return Store.DeleteAsync(BusinessCollections.Customers, customerId);
        }

        public Task<Dictionary<string, object?>?> GetCustomerAsync(string customerId)
        {
            return Store.GetByIdAsync<Dictionary<string, object?>>(BusinessCollections.Customers, customerId);
        }

        public async Task<ReplicationPushResponse> ReceivePushAsync(
            ReplicationPushRequest request,
            CancellationToken cancellationToken)
        {
            var result = await _ingestionService.IngestAsync(_nodeId, request.Operations, cancellationToken);
            return new ReplicationPushResponse
            {
                AcceptedCount = result.AcceptedCount
            };
        }

        public async Task<ReplicationPullResponse> ReceivePullAsync(
            ReplicationPullRequest request,
            CancellationToken cancellationToken)
        {
            var operations = await Store.GetOperationsAfterLogSequenceAsync(
                request.AfterLogSequence,
                request.BatchSize,
                cancellationToken);

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

    private sealed class InMemoryPeerReplicationClient : IPeerReplicationClient
    {
        private readonly Func<string, TestNode> _nodeResolver;

        public InMemoryPeerReplicationClient(Func<string, TestNode> nodeResolver)
        {
            _nodeResolver = nodeResolver;
        }

        public Task<ReplicationPushResponse> PushAsync(
            ClusterPeer peer,
            ReplicationPushRequest request,
            CancellationToken cancellationToken = default)
        {
            return _nodeResolver(peer.NodeId).ReceivePushAsync(request, cancellationToken);
        }

        public Task<ReplicationPullResponse> PullAsync(
            ClusterPeer peer,
            ReplicationPullRequest request,
            CancellationToken cancellationToken = default)
        {
            return _nodeResolver(peer.NodeId).ReceivePullAsync(request, cancellationToken);
        }
    }
}

