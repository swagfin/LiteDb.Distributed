using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Infrastructure.Conflict
{
    public class LastWriteWinsConflictResolver : IConflictResolver
    {
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

            DateTime incoming = context.IncomingOperation.TimestampUtc;
            DateTime local = context.LocalDocumentState.LastModifiedUtc;

            if (incoming >= local)
            {
                return Task.FromResult(new ConflictResolutionResult
                {
                    Action = ConflictResolutionAction.ApplyIncoming,
                    Reason = "Incoming operation wins by timestamp."
                });
            }

            return Task.FromResult(new ConflictResolutionResult
            {
                Action = ConflictResolutionAction.KeepLocal,
                Reason = "Local materialized document is newer."
            });
        }
    }


}
