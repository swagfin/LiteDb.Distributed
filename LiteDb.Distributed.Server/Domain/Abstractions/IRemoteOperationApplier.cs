using LiteDb.Distributed.Server.Domain.Models;

namespace LiteDb.Distributed.Server.Domain.Abstractions
{
    public interface IRemoteOperationApplier
    {
        Task<bool> ApplyRemoteOperationAsync(OperationRecord operation, CancellationToken cancellationToken = default);
    }

}
