using DistributedLiteDb.Core.Models;

namespace DistributedLiteDb.Core.Abstractions;

public interface IRemoteOperationApplier
{
    Task<bool> ApplyRemoteOperationAsync(
        OperationRecord operation,
        CancellationToken cancellationToken = default);
}
