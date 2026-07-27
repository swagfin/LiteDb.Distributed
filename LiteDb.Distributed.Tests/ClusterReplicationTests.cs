using LiteDb.Distributed.Server.Controllers;
using LiteDb.Distributed.Server.Core.Models;
using LiteDb.Distributed.Tests.TestSupport;

namespace LiteDb.Distributed.Tests
{
    public class ClusterReplicationTests
    {
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
    }
}
