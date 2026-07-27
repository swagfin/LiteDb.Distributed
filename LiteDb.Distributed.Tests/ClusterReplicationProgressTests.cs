using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Infrastructure.Conflict;
using LiteDb.Distributed.Server.Core.Models;
using LiteDb.Distributed.Server.Infrastructure.Replication;
using LiteDb.Distributed.Server.Data;
using LiteDb.Distributed.Tests.TestEntities;
using LiteDb.Distributed.Tests.TestSupport;
using LiteDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteDb.Distributed.Tests
{
    public class ClusterReplicationProgressTests
    {
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

                await store.UpsertAsync(TestCollections.Customer, "cust-progress-001", new Customer { Id = "cust-progress-001", Name = "One", Email = "one@example.com", UpdatedUtc = DateTime.UtcNow });
                await store.UpsertAsync(TestCollections.Customer, "cust-progress-002", new Customer { Id = "cust-progress-002", Name = "Two", Email = "two@example.com", UpdatedUtc = DateTime.UtcNow });

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
                    TestFileSystem.DeleteDirectoryIfExists(rootPath);
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

                await sourceStore.UpsertAsync(TestCollections.Customer, "cust-dup-001", new Customer { Id = "cust-dup-001", Name = "Dup", Email = "dup@example.com", UpdatedUtc = DateTime.UtcNow });
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
                    TestFileSystem.DeleteDirectoryIfExists(rootPath);
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
                    await store.UpsertAsync(TestCollections.Customer, "cust-single-file-001", new Customer { Id = "cust-single-file-001", Name = "Single File", Email = "single@example.com", UpdatedUtc = DateTime.UtcNow });
                }

                using LiteDatabase database = new LiteDatabase(databasePath);
                BsonDocument? customer = database.GetCollection<BsonDocument>(TestCollections.Customer).FindById("cust-single-file-001");
                BsonDocument? operation = database.GetCollection("_sys_operations").FindAll().FirstOrDefault();

                Assert.NotNull(customer);
                Assert.NotNull(operation);
                Assert.False(File.Exists(Path.Combine(rootPath, "node-a.testdb.db.metadata")));
            }
            finally
            {
                if (Directory.Exists(rootPath))
                {
                    TestFileSystem.DeleteDirectoryIfExists(rootPath);
                }
            }
        }

        [Fact]
        public async Task ReplicationStatus_ReportsPendingPushBeforeSync()
        {
            await using TestCluster cluster = new TestCluster();
            TestNode nodeA = cluster.AddNode("node-a");
            TestNode nodeB = cluster.AddNode("node-b");

            await cluster.ConnectAllAsync();
            await nodeA.UpsertCustomerAsync("cust-lag-001", "Lag One");

            ReplicationStatusSnapshot status = await nodeA.GetReplicationStatusAsync();
            ReplicationDatabaseStatus databaseStatus = Assert.Single(status.Databases);
            ReplicationPeerStatus peerStatus = Assert.Single(databaseStatus.Peers.Where(x => x.PeerNodeId == "node-b"));

            Assert.Equal(1, databaseStatus.OldestAvailableLogSequence);
            Assert.Equal(1, databaseStatus.LocalMaxLogSequence);
            Assert.Equal(1, databaseStatus.TotalEstimatedPendingPushOperations);
            Assert.Equal("CatchingUp", peerStatus.CatchUpStatus);
            Assert.Equal(1, peerStatus.EstimatedPendingPushOperations);
            Assert.Equal(0, peerStatus.LastPushedLocalLogSequence);
        }

        [Fact]
        public async Task ReplicationStatus_ReportsZeroPendingPushAfterSync()
        {
            await using TestCluster cluster = new TestCluster();
            TestNode nodeA = cluster.AddNode("node-a");
            TestNode nodeB = cluster.AddNode("node-b");

            await cluster.ConnectAllAsync();
            await nodeA.UpsertCustomerAsync("cust-lag-002", "Lag Two");
            await cluster.ReplicateAllAsync(rounds: 2);

            ReplicationStatusSnapshot status = await nodeA.GetReplicationStatusAsync();
            ReplicationDatabaseStatus databaseStatus = Assert.Single(status.Databases);
            ReplicationPeerStatus peerStatus = Assert.Single(databaseStatus.Peers.Where(x => x.PeerNodeId == "node-b"));

            Assert.Equal(1, databaseStatus.OldestAvailableLogSequence);
            Assert.Equal(1, databaseStatus.LocalMaxLogSequence);
            Assert.Equal(0, databaseStatus.TotalEstimatedPendingPushOperations);
            Assert.Equal("Ready", peerStatus.CatchUpStatus);
            Assert.Equal(0, peerStatus.EstimatedPendingPushOperations);
            Assert.Equal(1, peerStatus.LastPushedLocalLogSequence);
        }

        [Fact]
        public async Task ReplicationStatus_ReportsTooOldNeedsSnapshot_WhenPeerCheckpointFallsBehindPrunedLog()
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

                await store.UpsertPeerAsync(new ClusterPeer { NodeId = "node-b", BaseUrl = "inmemory://node-b", IsActive = true, UpdatedUtc = DateTime.UtcNow });
                await store.UpsertAsync(TestCollections.Customer, "cust-old-001", new Customer { Id = "cust-old-001", Name = "One", Email = "one@example.com", UpdatedUtc = DateTime.UtcNow });
                await store.UpsertAsync(TestCollections.Customer, "cust-old-002", new Customer { Id = "cust-old-002", Name = "Two", Email = "two@example.com", UpdatedUtc = DateTime.UtcNow });
                await store.UpsertAsync(TestCollections.Customer, "cust-old-003", new Customer { Id = "cust-old-003", Name = "Three", Email = "three@example.com", UpdatedUtc = DateTime.UtcNow });
                await store.PruneOperationLogAsync(throughLogSequence: 2, olderThanUtc: DateTime.UtcNow.AddDays(1), batchSize: 100);

                ReplicationStatusService statusService = new ReplicationStatusService(
                    new ClusterNodeOptions { NodeId = "node-a" },
                    new InMemoryLogicalDatabaseCatalog("testdb"),
                    new InMemoryLogicalDatabaseStoreProvider(store),
                    NullLogger<ReplicationStatusService>.Instance);

                ReplicationStatusSnapshot status = await statusService.GetStatusAsync();
                ReplicationDatabaseStatus databaseStatus = Assert.Single(status.Databases);
                ReplicationPeerStatus peerStatus = Assert.Single(databaseStatus.Peers);

                Assert.Equal(3, databaseStatus.OldestAvailableLogSequence);
                Assert.Equal(3, databaseStatus.LocalMaxLogSequence);
                Assert.Equal("TooOldNeedsSnapshot", peerStatus.CatchUpStatus);
                Assert.Contains("Restore from snapshot", peerStatus.CatchUpReason);
            }
            finally
            {
                if (Directory.Exists(rootPath))
                {
                    TestFileSystem.DeleteDirectoryIfExists(rootPath);
                }
            }
        }
    }
}
