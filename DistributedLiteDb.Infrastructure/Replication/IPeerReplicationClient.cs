using DistributedLiteDb.Core.Models;

namespace DistributedLiteDb.Infrastructure.Replication;

public interface IPeerReplicationClient
{
    Task<ReplicationPushResponse> PushAsync(
        ClusterPeer peer,
        ReplicationPushRequest request,
        CancellationToken cancellationToken = default);

    Task<ReplicationPullResponse> PullAsync(
        ClusterPeer peer,
        ReplicationPullRequest request,
        CancellationToken cancellationToken = default);
}
