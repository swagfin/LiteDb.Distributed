using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Infrastructure.Replication
{
    public interface IPeerReplicationClient
    {
        Task<ReplicationPushResponse> PushAsync(ClusterPeer peer, ReplicationPushRequest request, CancellationToken cancellationToken = default);

        Task<ReplicationPullResponse> PullAsync(ClusterPeer peer, ReplicationPullRequest request, CancellationToken cancellationToken = default);
    }



}

