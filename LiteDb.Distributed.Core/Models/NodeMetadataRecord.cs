namespace LiteDb.Distributed.Core.Models;

public sealed record NodeMetadataRecord
{
    public required string NodeId { get; init; }
    public long LastLocalSequence { get; init; }
    public DateTime LastWriteUtc { get; init; }
}

