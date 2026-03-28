using System.Net.Http.Json;
using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure.Context;

namespace LiteDb.Distributed.Infrastructure.Replication;

public sealed class HttpPeerReplicationClient : IPeerReplicationClient
{
    private readonly HttpClient _httpClient;
    private readonly IDatabaseContextAccessor _databaseContextAccessor;

    public HttpPeerReplicationClient(
        HttpClient httpClient,
        IDatabaseContextAccessor databaseContextAccessor)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _databaseContextAccessor = databaseContextAccessor ?? throw new ArgumentNullException(nameof(databaseContextAccessor));
    }

    public async Task<ReplicationPushResponse> PushAsync(
        ClusterPeer peer,
        ReplicationPushRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = BuildPeerUri(peer.BaseUrl, "/api/replication/push");
        var context = GetRequiredContext();

        using var response = await SendWithDatabaseHeadersAsync(endpoint, request, context, cancellationToken).ConfigureAwait(false);
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
        var context = GetRequiredContext();

        using var response = await SendWithDatabaseHeadersAsync(endpoint, request, context, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ReplicationPullResponse>(cancellationToken).ConfigureAwait(false);
        return payload ?? new ReplicationPullResponse { Operations = Array.Empty<OperationRecord>() };
    }

    private DatabaseRequestContext GetRequiredContext()
    {
        var context = _databaseContextAccessor.Current;
        if (context is null)
        {
            throw new InvalidOperationException("No active database context available for peer replication.");
        }

        return context;
    }

    private async Task<HttpResponseMessage> SendWithDatabaseHeadersAsync(
        Uri endpoint,
        object requestPayload,
        DatabaseRequestContext context,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(requestPayload)
        };

        request.Headers.Add("Database", context.DatabaseName);
        request.Headers.Add("ApiKey", context.Credential);

        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static Uri BuildPeerUri(string baseUrl, string relativePath)
    {
        var normalizedBase = baseUrl.TrimEnd('/');
        return new Uri($"{normalizedBase}{relativePath}", UriKind.Absolute);
    }
}

