using DistributedLiteDb.Core.Models;

namespace DistributedLiteDb.Core.Abstractions;

public interface IClusterPeerRegistry
{
    Task<IReadOnlyList<ClusterPeer>> GetPeersAsync(CancellationToken cancellationToken = default);

    Task UpsertPeerAsync(ClusterPeer peer, CancellationToken cancellationToken = default);
}
