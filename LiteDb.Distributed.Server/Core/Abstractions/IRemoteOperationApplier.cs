using LiteDb.Distributed.Server.Core.Models;

namespace LiteDb.Distributed.Server.Core.Abstractions
{
    public interface IRemoteOperationApplier
    {
        Task<bool> ApplyRemoteOperationAsync(OperationRecord operation, CancellationToken cancellationToken = default);
    }

}
