using LiteDb.Distributed.Server.Core.Models;

namespace LiteDb.Distributed.Server.Core.Abstractions
{
    public interface IConflictStore
    {
        Task RecordConflictAsync(ConflictRecord conflict, CancellationToken cancellationToken = default);
    }

}
