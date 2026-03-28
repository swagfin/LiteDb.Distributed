using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Infrastructure.Configuration;

public sealed record ClusterNodeOptions
{
    public required string NodeId { get; init; }
    public string DataDirectory { get; init; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
    public int ReplicationBatchSize { get; init; } = 1000;
    public int ReplicationPeerConcurrency { get; init; } = 4;
    public IReadOnlyList<string> CriticalCollections { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ClusterPeer> SeedPeers { get; init; } = Array.Empty<ClusterPeer>();
}

