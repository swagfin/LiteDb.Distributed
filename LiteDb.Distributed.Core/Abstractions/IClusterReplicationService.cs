

namespace LiteDb.Distributed.Core.Abstractions
{
    public interface IClusterReplicationService
    {
        Task ReplicateOnceAsync(CancellationToken cancellationToken = default);
    }


}
