using DistributedLiteDb.Core.Models;

namespace DistributedLiteDb.Infrastructure.Storage;

public sealed record LiteDbNodeStoreOptions
{
    public required string DatabasePath { get; init; }
    public required string NodeId { get; init; }
    public IReadOnlyList<ClusterPeer> SeedPeers { get; init; } = Array.Empty<ClusterPeer>();
}
