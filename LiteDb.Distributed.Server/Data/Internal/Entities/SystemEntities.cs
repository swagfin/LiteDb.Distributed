using LiteDB;

namespace LiteDb.Distributed.Server.Data.Internal.Entities
{
    internal class OperationEntity
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
        public string Collection { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;
        public int OperationType { get; set; }
        public string Payload { get; set; } = "{}";
        public long Sequence { get; set; }
        public long LogSequence { get; set; }
        public string? ParentVersion { get; set; }
        public bool IsSynced { get; set; }
        public bool IsTombstone { get; set; }
    }

    internal class OperationReceiptEntity
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
        public long LogSequence { get; set; }
        public DateTime TimestampUtc { get; set; }
        public DateTime PrunedUtc { get; set; }
    }

    internal class NodeMetadataEntity
    {
        [BsonId]
        public string NodeId { get; set; } = string.Empty;
        public long LastLocalSequence { get; set; }
        public DateTime LastWriteUtc { get; set; }
    }

    internal class ConflictEntity
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string NodeId { get; set; } = string.Empty;
        public string Collection { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;
        public string IncomingOperationId { get; set; } = string.Empty;
        public string LocalVersion { get; set; } = string.Empty;
        public string IncomingVersionHint { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public string? LocalPayload { get; set; }
        public string? IncomingPayload { get; set; }
    }

    internal class PeerCheckpointEntity
    {
        [BsonId]
        public string Id { get; set; } = string.Empty;
        public string LocalNodeId { get; set; } = string.Empty;
        public string PeerNodeId { get; set; } = string.Empty;
        public long LastPushedLocalLogSequence { get; set; }
        public long LastPulledPeerLogSequence { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    internal class ClusterPeerEntity
    {
        [BsonId]
        public string NodeId { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
