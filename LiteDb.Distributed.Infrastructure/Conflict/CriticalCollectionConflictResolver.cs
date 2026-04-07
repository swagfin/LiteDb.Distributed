using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Infrastructure.Conflict
{
    public class CriticalCollectionConflictResolver : IConflictResolver
    {
        private readonly IConflictResolver _inner;
        private readonly HashSet<string> _criticalCollections;

        public CriticalCollectionConflictResolver(IConflictResolver inner, IEnumerable<string> criticalCollections)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _criticalCollections = new HashSet<string>(criticalCollections ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public async Task<ConflictResolutionResult> ResolveAsync(ConflictResolutionContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(context);

            ConflictResolutionResult decision = await _inner.ResolveAsync(context, cancellationToken).ConfigureAwait(false);

            if (decision.Action != ConflictResolutionAction.KeepLocal)
            {
                return decision;
            }

            if (!_criticalCollections.Contains(context.IncomingOperation.Collection))
            {
                return decision;
            }

            return decision with
            {
                Action = ConflictResolutionAction.KeepLocalAndRecordConflict,
                Reason = string.IsNullOrWhiteSpace(decision.Reason)
                    ? "Conflict captured for a critical collection."
                    : decision.Reason
            };
        }
    }

}
