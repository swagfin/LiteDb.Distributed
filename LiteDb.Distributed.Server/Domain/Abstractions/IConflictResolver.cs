using LiteDb.Distributed.Server.Domain.Models;

namespace LiteDb.Distributed.Server.Domain.Abstractions
{
    public interface IConflictResolver
    {
        Task<ConflictResolutionResult> ResolveAsync(ConflictResolutionContext context, CancellationToken cancellationToken = default);
    }

}
