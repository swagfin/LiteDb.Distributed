using LiteDb.Distributed.Server.Core.Models;

namespace LiteDb.Distributed.Server.Core.Abstractions
{
    public interface IConflictResolver
    {
        Task<ConflictResolutionResult> ResolveAsync(ConflictResolutionContext context, CancellationToken cancellationToken = default);
    }

}
