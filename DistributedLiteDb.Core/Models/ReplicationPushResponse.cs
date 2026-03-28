namespace DistributedLiteDb.Core.Models;

public sealed record ReplicationPushResponse
{
    public required int AcceptedCount { get; init; }
}
