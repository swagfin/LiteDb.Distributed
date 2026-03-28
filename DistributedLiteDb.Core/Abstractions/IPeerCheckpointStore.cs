using DistributedLiteDb.Core.Models;

namespace DistributedLiteDb.Core.Abstractions;

public interface IPeerCheckpointStore
{
    Task<PeerCheckpointRecord> GetOrCreatePeerCheckpointAsync(
        string localNodeId,
        string peerNodeId,
        CancellationToken cancellationToken = default);

    Task SavePeerCheckpointAsync(
        PeerCheckpointRecord checkpoint,
        CancellationToken cancellationToken = default);
}
