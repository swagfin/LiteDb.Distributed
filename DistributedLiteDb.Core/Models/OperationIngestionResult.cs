namespace DistributedLiteDb.Core.Models;

public sealed record OperationIngestionResult
{
    public required int AcceptedCount { get; init; }
    public required int ConflictCount { get; init; }
}
