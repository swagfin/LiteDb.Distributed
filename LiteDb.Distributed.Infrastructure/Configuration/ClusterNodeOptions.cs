using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Infrastructure.Configuration;

public sealed record ClusterNodeOptions
{
    public required string NodeId { get; init; }
    public required string DatabasePath { get; init; }
    public int ReplicationIntervalSeconds { get; init; } = 5;
    public int ReplicationBatchSize { get; init; } = 200;
    public IReadOnlyList<string> CriticalCollections { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ClusterPeer> SeedPeers { get; init; } = Array.Empty<ClusterPeer>();
}

