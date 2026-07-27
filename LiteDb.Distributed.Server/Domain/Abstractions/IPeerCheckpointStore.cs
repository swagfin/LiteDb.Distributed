using LiteDb.Distributed.Server.Domain.Models;

namespace LiteDb.Distributed.Server.Domain.Abstractions
{
    public interface IPeerCheckpointStore
    {
        Task<PeerCheckpointRecord> GetOrCreatePeerCheckpointAsync(string localNodeId, string peerNodeId, CancellationToken cancellationToken = default);

        Task SavePeerCheckpointAsync(PeerCheckpointRecord checkpoint, CancellationToken cancellationToken = default);
    }

}
