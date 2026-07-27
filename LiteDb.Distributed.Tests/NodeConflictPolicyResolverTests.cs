using LiteDb.Distributed.Server.Domain.Models;
using LiteDb.Distributed.Server.Conflict;

namespace LiteDb.Distributed.Tests
{
    public class NodeConflictPolicyResolverTests
    {
        [Fact]
        public async Task ResolveAsync_ReturnsApplyIncoming_WhenPolicyIsApplyIncoming()
        {
            NodeConflictPolicyResolver resolver = new NodeConflictPolicyResolver("ApplyIncoming");
            ConflictResolutionContext context = CreateContext(hasLocalState: true);

            ConflictResolutionResult result = await resolver.ResolveAsync(context);

            Assert.Equal(ConflictResolutionAction.ApplyIncoming, result.Action);
        }

        [Fact]
        public async Task ResolveAsync_ReturnsKeepLocal_WhenPolicyIsKeepLocalAndDocumentExistsLocally()
        {
            NodeConflictPolicyResolver resolver = new NodeConflictPolicyResolver("KeepLocal");
            ConflictResolutionContext context = CreateContext(hasLocalState: true);

            ConflictResolutionResult result = await resolver.ResolveAsync(context);

            Assert.Equal(ConflictResolutionAction.KeepLocal, result.Action);
        }

        [Fact]
        public async Task ResolveAsync_ReturnsApplyIncoming_WhenLocalDocumentDoesNotExist()
        {
            NodeConflictPolicyResolver resolver = new NodeConflictPolicyResolver("KeepLocal");
            ConflictResolutionContext context = CreateContext(hasLocalState: false);

            ConflictResolutionResult result = await resolver.ResolveAsync(context);

            Assert.Equal(ConflictResolutionAction.ApplyIncoming, result.Action);
        }

        [Fact]
        public void Constructor_Throws_WhenPolicyValueIsInvalid()
        {
            Assert.Throws<ArgumentException>(() => new NodeConflictPolicyResolver("unknown"));
        }

        private static ConflictResolutionContext CreateContext(bool hasLocalState)
        {
            DocumentState? localState = hasLocalState
                ? new DocumentState
                {
                    Collection = "customers",
                    EntityId = "cust-001",
                    Version = "v-local",
                    LastWriterNodeId = "node-a",
                    LastModifiedUtc = DateTime.UtcNow,
                    IsDeleted = false,
                    Payload = "{\"Id\":\"cust-001\"}"
                }
                : null;

            return new ConflictResolutionContext
            {
                LocalNodeId = "node-a",
                IncomingOperation = new OperationRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    NodeId = "node-b",
                    Collection = "customers",
                    EntityId = "cust-001",
                    OperationType = OperationType.Update,
                    TimestampUtc = DateTime.UtcNow,
                    ParentVersion = "v-parent",
                    Payload = "{\"Id\":\"cust-001\",\"Name\":\"Incoming\"}",
                    Sequence = 1,
                    LogSequence = 1
                },
                LocalDocumentState = localState
            };
        }
    }
}
