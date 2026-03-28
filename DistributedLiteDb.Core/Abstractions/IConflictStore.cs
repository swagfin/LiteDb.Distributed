using DistributedLiteDb.Core.Models;

namespace DistributedLiteDb.Core.Abstractions;

public interface IConflictStore
{
    Task RecordConflictAsync(ConflictRecord conflict, CancellationToken cancellationToken = default);
}
