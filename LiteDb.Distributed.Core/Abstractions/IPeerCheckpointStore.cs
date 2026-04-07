using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Core.Abstractions
{
    public interface IPeerCheckpointStore
    {
        Task<PeerCheckpointRecord> GetOrCreatePeerCheckpointAsync(string localNodeId, string peerNodeId, CancellationToken cancellationToken = default);

        Task SavePeerCheckpointAsync(PeerCheckpointRecord checkpoint, CancellationToken cancellationToken = default);
    }

}
