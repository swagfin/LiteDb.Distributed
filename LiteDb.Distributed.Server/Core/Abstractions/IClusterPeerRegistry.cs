using LiteDb.Distributed.Server.Core.Models;

namespace LiteDb.Distributed.Server.Core.Abstractions
{
    public interface IClusterPeerRegistry
    {
        Task<IReadOnlyList<ClusterPeer>> GetPeersAsync(CancellationToken cancellationToken = default);

        Task UpsertPeerAsync(ClusterPeer peer, CancellationToken cancellationToken = default);
    }

}
