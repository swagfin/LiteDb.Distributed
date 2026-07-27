using LiteDb.Distributed.Server.Storage.Internal.Entities;
using LiteDB;

namespace LiteDb.Distributed.Server.Storage.Internal.Schema
{
    internal static class LiteDbIndexes
    {
        public static void EnsureSystemIndexes(
            ILiteCollection<OperationEntity> operations,
            ILiteCollection<OperationReceiptEntity> operationReceipts,
            ILiteCollection<NodeMetadataEntity> nodeMetadata,
            ILiteCollection<ConflictEntity> conflicts,
            ILiteCollection<PeerCheckpointEntity> peerCheckpoints,
            ILiteCollection<ClusterPeerEntity> clusterPeers)
        {
            operations.EnsureIndex(x => x.NodeId);
            operations.EnsureIndex(x => x.Sequence);
            operations.EnsureIndex(x => x.LogSequence);
            operations.EnsureIndex(x => x.TimestampUtc);
            operationReceipts.EnsureIndex(x => x.PrunedUtc);

            nodeMetadata.EnsureIndex(x => x.NodeId, unique: true);
            conflicts.EnsureIndex(x => x.CreatedUtc);
            peerCheckpoints.EnsureIndex(x => x.Id, unique: true);
            clusterPeers.EnsureIndex(x => x.NodeId, unique: true);
        }
    }
}
