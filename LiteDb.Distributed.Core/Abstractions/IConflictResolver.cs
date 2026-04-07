using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Core.Abstractions
{
    public interface IConflictResolver
    {
        Task<ConflictResolutionResult> ResolveAsync(ConflictResolutionContext context, CancellationToken cancellationToken = default);
    }


}
