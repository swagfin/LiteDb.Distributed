using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Core.Abstractions;

public interface IRemoteOperationApplier
{
    Task<bool> ApplyRemoteOperationAsync(OperationRecord operation, CancellationToken cancellationToken = default);
}


