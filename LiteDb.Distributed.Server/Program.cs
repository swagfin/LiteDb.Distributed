using System.Text.Json;
using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Exceptions;
using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Context;
using LiteDb.Distributed.Infrastructure.Storage;

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

app.Use(async (httpContext, next) =>
{
    if (!httpContext.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        await next().ConfigureAwait(false);
        return;
    }

    var contextResolver = httpContext.RequestServices.GetRequiredService<IDatabaseRequestContextResolver>();
    var contextAccessor = httpContext.RequestServices.GetRequiredService<IDatabaseContextAccessor>();

    try
    {
        var databaseContext = await contextResolver
            .ResolveAsync(httpContext.Request.Headers, httpContext.RequestAborted)
            .ConfigureAwait(false);

        using var scope = contextAccessor.BeginScope(databaseContext);
        await next().ConfigureAwait(false);
    }
    catch (DatabaseAuthenticationException ex)
    {
        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await httpContext.Response.WriteAsJsonAsync(new { Error = ex.Message }, httpContext.RequestAborted).ConfigureAwait(false);
    }
    catch (ArgumentException ex)
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(new { Error = ex.Message }, httpContext.RequestAborted).ConfigureAwait(false);
    }
});

app.MapGet("/", async (
    ILogicalDatabaseCatalog databaseCatalog,
    CancellationToken cancellationToken) =>
{
    var databases = await databaseCatalog.GetAllAsync(cancellationToken).ConfigureAwait(false);

    return Results.Ok(new
    {
        NodeId = nodeOptions.NodeId,
        TimestampUtc = DateTime.UtcNow,
        LogicalDatabaseCount = databases.Count
    });
});

app.MapGet("/api/{documentName}", async (
    string documentName,
    int skip,
    int take,
    ILocalDocumentReader reader,
    CancellationToken cancellationToken) =>
{
    var safeTake = take <= 0 ? 100 : take;

    var documents = await reader
        .ListAsync<Dictionary<string, object?>>(documentName, skip, safeTake, cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(documents);
});

app.MapGet("/api/{documentName}/{id}", async (
    string documentName,
    string id,
    ILocalDocumentReader reader,
    CancellationToken cancellationToken) =>
{
    var document = await reader
        .GetByIdAsync<Dictionary<string, object?>>(documentName, id, cancellationToken)
        .ConfigureAwait(false);

    return document is null
        ? Results.NotFound()
        : Results.Ok(document);
});

app.MapPost("/api/{documentName}", async (
    string documentName,
    JsonElement payload,
    string? parentVersion,
    ILocalDocumentWriter writer,
    CancellationToken cancellationToken) =>
{
    if (!TryExtractEntityId(payload, out var entityId))
    {
        return Results.BadRequest(new { Error = "POST body must include an 'Id' string field." });
    }

    try
    {
        var result = await writer
            .UpsertAsync(documentName, entityId, payload, parentVersion, cancellationToken)
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

app.MapPut("/api/{documentName}/{id}", async (
    string documentName,
    string id,
    JsonElement payload,
    string? parentVersion,
    ILocalDocumentWriter writer,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await writer
            .UpsertAsync(documentName, id, payload, parentVersion, cancellationToken)
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

app.MapDelete("/api/{documentName}/{id}", async (
    string documentName,
    string id,
    string? parentVersion,
    ILocalDocumentWriter writer,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await writer
            .DeleteAsync(documentName, id, parentVersion, cancellationToken)
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

static bool TryExtractEntityId(JsonElement payload, out string entityId)
{
    entityId = string.Empty;

    if (payload.ValueKind != JsonValueKind.Object)
    {
        return false;
    }

    if (TryReadPropertyAsString(payload, "Id", out entityId))
    {
        return true;
    }

    if (TryReadPropertyAsString(payload, "id", out entityId))
    {
        return true;
    }

    return false;
}

static bool TryReadPropertyAsString(JsonElement payload, string propertyName, out string value)
{
    value = string.Empty;

    if (!payload.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
    {
        return false;
    }

    var candidate = property.GetString();
    if (string.IsNullOrWhiteSpace(candidate))
    {
        return false;
    }

    value = candidate;
    return true;
}
