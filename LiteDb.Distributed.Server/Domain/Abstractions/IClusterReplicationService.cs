

namespace LiteDb.Distributed.Server.Domain.Abstractions
{
    public interface IClusterReplicationService
    {
        Task ReplicateOnceAsync(CancellationToken cancellationToken = default);
    }

}
