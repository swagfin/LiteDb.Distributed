using DistributedLiteDb.Core.Abstractions;
using DistributedLiteDb.Core.Models;

namespace DistributedLiteDb.Infrastructure.Conflict;

public sealed class LastWriteWinsConflictResolver : IConflictResolver
{
    public Task<ConflictResolutionResult> ResolveAsync(
        ConflictResolutionContext context,
        CancellationToken cancellationToken = default)
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

        var incoming = context.IncomingOperation.TimestampUtc;
        var local = context.LocalDocumentState.LastModifiedUtc;

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
