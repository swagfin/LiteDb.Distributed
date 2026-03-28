namespace LiteDb.Distributed.Core.Models;

public sealed record PeerCheckpointRecord
{
    public required string LocalNodeId { get; init; }
    public required string PeerNodeId { get; init; }
    public long LastPushedLocalLogSequence { get; init; }
    public long LastPulledPeerLogSequence { get; init; }
    public DateTime UpdatedUtc { get; init; }
}

