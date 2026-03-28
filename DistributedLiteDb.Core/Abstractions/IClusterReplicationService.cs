namespace DistributedLiteDb.Core.Abstractions;

public interface IClusterReplicationService
{
    Task ReplicateOnceAsync(CancellationToken cancellationToken = default);
}
