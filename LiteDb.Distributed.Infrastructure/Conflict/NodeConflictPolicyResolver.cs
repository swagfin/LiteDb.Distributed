using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Infrastructure.Conflict
{
    public class NodeConflictPolicyResolver : IConflictResolver
    {
        private const string ApplyIncomingPolicy = "ApplyIncoming";
        private const string KeepLocalPolicy = "KeepLocal";
        private readonly string _policy;

        public NodeConflictPolicyResolver(string conflictResolutionPolicy)
        {
            if (string.IsNullOrWhiteSpace(conflictResolutionPolicy))
            {
                throw new ArgumentException("ConflictResolutionPolicy is required.", nameof(conflictResolutionPolicy));
            }

            string normalizedPolicy = conflictResolutionPolicy.Trim();
            if (!string.Equals(normalizedPolicy, ApplyIncomingPolicy, StringComparison.OrdinalIgnoreCase) && !string.Equals(normalizedPolicy, KeepLocalPolicy, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("ConflictResolutionPolicy must be either 'ApplyIncoming' or 'KeepLocal'.", nameof(conflictResolutionPolicy));
            }

            _policy = normalizedPolicy;
        }

        public Task<ConflictResolutionResult> ResolveAsync(ConflictResolutionContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(context);

            if (context.LocalDocumentState is null)
            {
                return Task.FromResult(new ConflictResolutionResult
                {
                    Action = ConflictResolutionAction.ApplyIncoming,
                    Reason = "No local materialized document exists."
                });
            }

            if (string.Equals(_policy, ApplyIncomingPolicy, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new ConflictResolutionResult
                {
                    Action = ConflictResolutionAction.ApplyIncoming,
                    Reason = "Node conflict policy is ApplyIncoming."
                });
            }

            return Task.FromResult(new ConflictResolutionResult
            {
                Action = ConflictResolutionAction.KeepLocal,
                Reason = "Node conflict policy is KeepLocal."
            });
        }
    }
}
