using System.Net.Http.Json;
using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Infrastructure.Replication;

public sealed class HttpPeerReplicationClient : IPeerReplicationClient
{
    private readonly HttpClient _httpClient;

    public HttpPeerReplicationClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ReplicationPushResponse> PushAsync(
        ClusterPeer peer,
        ReplicationPushRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = BuildPeerUri(peer.BaseUrl, "/api/replication/push");

        using var response = await _httpClient.PostAsJsonAsync(endpoint, request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ReplicationPushResponse>(cancellationToken).ConfigureAwait(false);
        return payload ?? new ReplicationPushResponse { AcceptedCount = 0 };
    }

    public async Task<ReplicationPullResponse> PullAsync(
        ClusterPeer peer,
        ReplicationPullRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = BuildPeerUri(peer.BaseUrl, "/api/replication/pull");

        using var response = await _httpClient.PostAsJsonAsync(endpoint, request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ReplicationPullResponse>(cancellationToken).ConfigureAwait(false);
        return payload ?? new ReplicationPullResponse { Operations = Array.Empty<OperationRecord>() };
    }

    private static Uri BuildPeerUri(string baseUrl, string relativePath)
    {
        var normalizedBase = baseUrl.TrimEnd('/');
        return new Uri($"{normalizedBase}{relativePath}", UriKind.Absolute);
    }
}

