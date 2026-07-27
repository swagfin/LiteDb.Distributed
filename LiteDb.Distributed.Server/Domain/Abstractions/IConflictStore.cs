using LiteDb.Distributed.Server.Domain.Models;

namespace LiteDb.Distributed.Server.Domain.Abstractions
{
    public interface IConflictStore
    {
        Task RecordConflictAsync(ConflictRecord conflict, CancellationToken cancellationToken = default);
    }

}
