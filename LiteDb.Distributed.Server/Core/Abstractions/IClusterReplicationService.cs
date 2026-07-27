

namespace LiteDb.Distributed.Server.Core.Abstractions
{
    public interface IClusterReplicationService
    {
        Task ReplicateOnceAsync(CancellationToken cancellationToken = default);
    }

}
