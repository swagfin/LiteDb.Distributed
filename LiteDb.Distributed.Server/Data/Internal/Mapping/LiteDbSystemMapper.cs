using LiteDb.Distributed.Server.Core.Models;
using LiteDb.Distributed.Server.Data.Internal.Entities;

namespace LiteDb.Distributed.Server.Data.Internal.Mapping
{
    internal static class LiteDbSystemMapper
    {
        public static OperationRecord MapToOperationRecord(OperationEntity entity)
        {
            return new OperationRecord
            {
                Id = entity.Id,
                NodeId = entity.NodeId,
                TimestampUtc = entity.TimestampUtc,
                Collection = entity.Collection,
                EntityId = entity.EntityId,
                OperationType = (OperationType)entity.OperationType,
                Payload = entity.Payload,
                Sequence = entity.Sequence,
                LogSequence = entity.LogSequence,
                ParentVersion = entity.ParentVersion,
                GlobalSequence = null,
                IsSynced = entity.IsSynced,
                IsTombstone = entity.IsTombstone
            };
        }

        public static OperationEntity MapToOperationEntity(OperationRecord operation)
        {
            return new OperationEntity
            {
                Id = operation.Id,
                NodeId = operation.NodeId,
                TimestampUtc = operation.TimestampUtc,
                Collection = operation.Collection,
                EntityId = operation.EntityId,
                OperationType = (int)operation.OperationType,
                Payload = operation.Payload,
                Sequence = operation.Sequence,
                LogSequence = operation.LogSequence,
                ParentVersion = operation.ParentVersion,
                IsSynced = operation.IsSynced,
                IsTombstone = operation.IsTombstone
            };
        }

        public static PeerCheckpointRecord MapToPeerCheckpointRecord(PeerCheckpointEntity entity)
        {
            return new PeerCheckpointRecord
            {
                LocalNodeId = entity.LocalNodeId,
                PeerNodeId = entity.PeerNodeId,
                LastPushedLocalLogSequence = entity.LastPushedLocalLogSequence,
                LastPulledPeerLogSequence = entity.LastPulledPeerLogSequence,
                UpdatedUtc = entity.UpdatedUtc
            };
        }

        public static ClusterPeer MapToClusterPeer(ClusterPeerEntity entity)
        {
            return new ClusterPeer
            {
                NodeId = entity.NodeId,
                BaseUrl = entity.BaseUrl,
                IsActive = entity.IsActive,
                UpdatedUtc = entity.UpdatedUtc
            };
        }
    }
}
