using LiteDb.Distributed.Server.Core.Models;

namespace LiteDb.Distributed.Server.Core.Abstractions
{
    public interface IPeerCheckpointStore
    {
        Task<PeerCheckpointRecord> GetOrCreatePeerCheckpointAsync(string localNodeId, string peerNodeId, CancellationToken cancellationToken = default);

        Task SavePeerCheckpointAsync(PeerCheckpointRecord checkpoint, CancellationToken cancellationToken = default);
    }

}
