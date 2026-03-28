using System.Text.Json;
using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Exceptions;
using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure;
using LiteDb.Distributed.Infrastructure.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:1446");

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
});

var nodeOptions = new ClusterNodeOptions
{
    NodeId = builder.Configuration["Node:NodeId"] ?? "node-1",
    ReplicationIntervalSeconds = builder.Configuration.GetValue<int?>("Node:ReplicationIntervalSeconds") ?? 5,
    ReplicationBatchSize = builder.Configuration.GetValue<int?>("Node:ReplicationBatchSize") ?? 200,
    CriticalCollections = builder.Configuration.GetSection("Node:CriticalCollections").Get<string[]>() ?? Array.Empty<string>(),
    SeedPeers = builder.Configuration.GetSection("Node:SeedPeers").Get<ClusterPeer[]>() ?? Array.Empty<ClusterPeer>()
};

builder.Services.AddLiteDbDistributedNode(nodeOptions);

var app = builder.Build();

app.MapGet("/", async (
    IClusterPeerRegistry peerRegistry,
    CancellationToken cancellationToken) =>
{
    var peers = await peerRegistry.GetPeersAsync(cancellationToken).ConfigureAwait(false);

    return Results.Ok(new
    {
        NodeId = nodeOptions.NodeId,
        TimestampUtc = DateTime.UtcNow,
        PeerCount = peers.Count
    });
});

app.MapPut("/api/documents/{collection}/{id}", async (
    string collection,
    string id,
    JsonElement payload,
    string? parentVersion,
    ILocalDocumentWriter writer,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await writer
            .UpsertAsync(collection, id, payload, parentVersion, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(result);
    }
    catch (VersionMismatchException ex)
    {
        return Results.Conflict(new { Error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { Error = ex.Message });
    }
});

app.MapDelete("/api/documents/{collection}/{id}", async (
    string collection,
    string id,
    string? parentVersion,
    ILocalDocumentWriter writer,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await writer
            .DeleteAsync(collection, id, parentVersion, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(result);
    }
    catch (VersionMismatchException ex)
    {
        return Results.Conflict(new { Error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { Error = ex.Message });
    }
});

app.MapGet("/api/documents/{collection}/{id}", async (
    string collection,
    string id,
    ILocalDocumentReader reader,
    CancellationToken cancellationToken) =>
{
    var document = await reader
        .GetByIdAsync<Dictionary<string, object?>>(collection, id, cancellationToken)
        .ConfigureAwait(false);

    return document is null
        ? Results.NotFound()
        : Results.Ok(document);
});

app.MapGet("/api/documents/{collection}", async (
    string collection,
    int skip,
    int take,
    ILocalDocumentReader reader,
    CancellationToken cancellationToken) =>
{
    var safeTake = take <= 0 ? 100 : take;
    var documents = await reader
        .ListAsync<Dictionary<string, object?>>(collection, skip, safeTake, cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(documents);
});

app.MapPost("/api/replication/push", async (
    ReplicationPushRequest request,
    IOperationIngestionService ingestionService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await ingestionService
            .IngestAsync(nodeOptions.NodeId, request.Operations, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new ReplicationPushResponse
        {
            AcceptedCount = result.AcceptedCount
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { Error = ex.Message });
    }
});

app.MapPost("/api/replication/pull", async (
    ReplicationPullRequest request,
    IOperationLogStore operationLogStore,
    CancellationToken cancellationToken) =>
{
    if (request.BatchSize <= 0)
    {
        return Results.BadRequest(new { Error = "BatchSize must be greater than zero." });
    }

    var operations = await operationLogStore
        .GetOperationsAfterLogSequenceAsync(request.AfterLogSequence, request.BatchSize, cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(new ReplicationPullResponse
    {
        Operations = operations
    });
});

app.MapPost("/api/cluster/peers", async (
    ClusterPeer peer,
    IClusterPeerRegistry peerRegistry,
    CancellationToken cancellationToken) =>
{
    if (string.Equals(peer.NodeId, nodeOptions.NodeId, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { Error = "Cannot register local node as a peer." });
    }

    await peerRegistry.UpsertPeerAsync(peer with { UpdatedUtc = DateTime.UtcNow }, cancellationToken)
        .ConfigureAwait(false);
    return Results.Ok();
});

app.MapGet("/api/cluster/peers", async (
    IClusterPeerRegistry peerRegistry,
    CancellationToken cancellationToken) =>
{
    var peers = await peerRegistry.GetPeersAsync(cancellationToken).ConfigureAwait(false);
    return Results.Ok(peers);
});

app.MapPost("/api/replication/trigger", async (
    IClusterReplicationService replicationService,
    CancellationToken cancellationToken) =>
{
    await replicationService.ReplicateOnceAsync(cancellationToken).ConfigureAwait(false);
    return Results.Accepted();
});

app.Run();

