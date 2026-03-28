namespace DistributedLiteDb.Core.Models;

public sealed record ConflictResolutionContext
{
    public required string LocalNodeId { get; init; }
    public required OperationRecord IncomingOperation { get; init; }
    public DocumentState? LocalDocumentState { get; init; }
}
