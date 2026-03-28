using DistributedLiteDb.Core.Models;

namespace DistributedLiteDb.Core.Abstractions;

public interface IConflictResolver
{
    Task<ConflictResolutionResult> ResolveAsync(
        ConflictResolutionContext context,
        CancellationToken cancellationToken = default);
}
