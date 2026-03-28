using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Infrastructure.Storage;

public sealed record LiteDbNodeStoreOptions
{
    public required string DatabaseName { get; init; }
    public required string BusinessDatabasePath { get; init; }
    public required string MetadataDatabasePath { get; init; }
    public required string NodeId { get; init; }
    public IReadOnlyList<ClusterPeer> SeedPeers { get; init; } = Array.Empty<ClusterPeer>();
}

