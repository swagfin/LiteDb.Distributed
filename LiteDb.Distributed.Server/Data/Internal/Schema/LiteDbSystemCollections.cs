using LiteDb.Distributed.Server.Core.Collections;

namespace LiteDb.Distributed.Server.Data.Internal.Schema
{
    internal static class LiteDbSystemCollections
    {
        public const string Operations = SystemCollections.Operations;
        public const string NodeMetadata = SystemCollections.NodeMetadata;
        public const string Conflicts = SystemCollections.Conflicts;
        public const string PeerCheckpoints = "_sys_peer_checkpoints";
        public const string ClusterPeers = "_sys_cluster_peers";
        public const string OperationReceipts = "_sys_operation_receipts";
    }
}
