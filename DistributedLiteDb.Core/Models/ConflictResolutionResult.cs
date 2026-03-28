namespace DistributedLiteDb.Core.Models;

public sealed record ConflictResolutionResult
{
    public required ConflictResolutionAction Action { get; init; }
    public string Reason { get; init; } = string.Empty;
}
