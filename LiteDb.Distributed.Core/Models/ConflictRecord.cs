namespace LiteDb.Distributed.Core.Models;

public sealed record ConflictRecord
{
    public required string Id { get; init; }
    public required string NodeId { get; init; }
    public required string Collection { get; init; }
    public required string EntityId { get; init; }
    public required string IncomingOperationId { get; init; }
    public required string LocalVersion { get; init; }
    public required string IncomingVersionHint { get; init; }
    public required string Reason { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string? LocalPayload { get; init; }
    public string? IncomingPayload { get; init; }
}

