using LiteDb.Distributed.Server.Conflict;
using LiteDb.Distributed.Server.Domain.Models;
using LiteDb.Distributed.Server.Replication;
using LiteDb.Distributed.Server.Storage;
using LiteDb.Distributed.Tests.TestEntities;
using LiteDb.Distributed.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiteDb.Distributed.Tests
{
    public class OperationLogPruningTests
    {
        [Fact]
        public async Task OperationLogPruning_PrunesOnlyOperationsCoveredByActivePeerCheckpoints()
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
                await store.UpsertAsync(TestCollections.Customer, "cust-prune-001", new Customer { Id = "cust-prune-001", Name = "One", Email = "one@example.com", UpdatedUtc = DateTime.UtcNow });
                await store.UpsertAsync(TestCollections.Customer, "cust-prune-002", new Customer { Id = "cust-prune-002", Name = "Two", Email = "two@example.com", UpdatedUtc = DateTime.UtcNow });
                await store.UpsertAsync(TestCollections.Customer, "cust-prune-003", new Customer { Id = "cust-prune-003", Name = "Three", Email = "three@example.com", UpdatedUtc = DateTime.UtcNow });
                await store.SavePeerCheckpointAsync(new PeerCheckpointRecord
                {
                    LocalNodeId = "node-a",
                    PeerNodeId = "node-b",
                    LastPushedLocalLogSequence = 2,
                    LastPulledPeerLogSequence = 0,
                    UpdatedUtc = DateTime.UtcNow
                });

                OperationLogPruneResult result = await store.PruneOperationLogAsync(throughLogSequence: 2, olderThanUtc: DateTime.UtcNow.AddDays(1), batchSize: 100);
                IReadOnlyList<OperationRecord> remaining = await store.GetOperationsAfterLogSequenceAsync(0, 100);

                Assert.Equal(2, result.PrunedCount);
                Assert.Equal(2, result.MaxPrunedLogSequence);
                Assert.Single(remaining);
                Assert.Equal("cust-prune-003", remaining[0].EntityId);
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
        public async Task OperationLogPruning_KeepsReceiptSoDuplicatePrunedOperationIsNotReapplied()
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

                await sourceStore.UpsertAsync(TestCollections.Customer, "cust-prune-dup-001", new Customer { Id = "cust-prune-dup-001", Name = "Original", Email = "original@example.com", UpdatedUtc = DateTime.UtcNow });
                OperationRecord operation = (await sourceStore.GetOperationsAfterLogSequenceAsync(0, 10)).Single();
                OperationIngestionService ingestionService = new OperationIngestionService(targetStore, new NodeConflictPolicyResolver("ApplyIncoming"), targetStore, targetStore, NullLogger<OperationIngestionService>.Instance);

                OperationIngestionResult first = await ingestionService.IngestAsync("node-b", new[] { operation });
                OperationLogPruneResult pruneResult = await targetStore.PruneOperationLogAsync(throughLogSequence: 1, olderThanUtc: DateTime.UtcNow.AddDays(1), batchSize: 100);
                OperationIngestionResult second = await ingestionService.IngestAsync("node-b", new[] { operation });

                Assert.Equal(1, first.AcceptedCount);
                Assert.Equal(1, pruneResult.PrunedCount);
                Assert.Equal(1, second.ProcessedCount);
                Assert.Equal(0, second.AcceptedCount);
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
        public async Task OperationReceiptPruning_RemovesReceiptsOlderThanRetentionCutoff()
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

                await sourceStore.UpsertAsync(TestCollections.Customer, "cust-receipt-prune-001", new Customer { Id = "cust-receipt-prune-001", Name = "Receipt", Email = "receipt@example.com", UpdatedUtc = DateTime.UtcNow });
                OperationRecord operation = (await sourceStore.GetOperationsAfterLogSequenceAsync(0, 10)).Single();
                OperationIngestionService ingestionService = new OperationIngestionService(targetStore, new NodeConflictPolicyResolver("ApplyIncoming"), targetStore, targetStore, NullLogger<OperationIngestionService>.Instance);

                await ingestionService.IngestAsync("node-b", new[] { operation });
                await targetStore.PruneOperationLogAsync(throughLogSequence: 1, olderThanUtc: DateTime.UtcNow.AddDays(1), batchSize: 100);

                OperationReceiptPruneResult retained = await targetStore.PruneOperationReceiptsAsync(DateTime.UtcNow.AddDays(-1), batchSize: 100);
                OperationIngestionResult duplicateBeforeReceiptPruned = await ingestionService.IngestAsync("node-b", new[] { operation });
                OperationReceiptPruneResult pruned = await targetStore.PruneOperationReceiptsAsync(DateTime.UtcNow.AddDays(1), batchSize: 100);
                OperationIngestionResult duplicateAfterReceiptPruned = await ingestionService.IngestAsync("node-b", new[] { operation });

                Assert.Equal(0, retained.PrunedCount);
                Assert.Equal(1, duplicateBeforeReceiptPruned.ProcessedCount);
                Assert.Equal(0, duplicateBeforeReceiptPruned.AcceptedCount);
                Assert.Equal(1, pruned.PrunedCount);
                Assert.Equal(1, duplicateAfterReceiptPruned.ProcessedCount);
                Assert.Equal(1, duplicateAfterReceiptPruned.AcceptedCount);
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
