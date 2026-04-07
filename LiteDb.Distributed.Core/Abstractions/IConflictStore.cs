using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Core.Abstractions
{
    public interface IConflictStore
    {
        Task RecordConflictAsync(ConflictRecord conflict, CancellationToken cancellationToken = default);
    }


}
