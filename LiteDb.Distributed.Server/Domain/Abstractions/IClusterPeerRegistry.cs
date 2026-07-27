using LiteDb.Distributed.Server.Domain.Models;

namespace LiteDb.Distributed.Server.Domain.Abstractions
{
    public interface IClusterPeerRegistry
    {
        Task<IReadOnlyList<ClusterPeer>> GetPeersAsync(CancellationToken cancellationToken = default);

        Task UpsertPeerAsync(ClusterPeer peer, CancellationToken cancellationToken = default);
    }

}
