using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Context;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:1446");

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = null;
    });

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
var logger = app.Logger;

app.UseDefaultFiles();
app.UseStaticFiles();

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
        logger.LogDebug(
            "Resolving database context for request. Method={Method} Path={Path}",
            httpContext.Request.Method,
            httpContext.Request.Path.Value);

        var databaseContext = await contextResolver
            .ResolveAsync(httpContext.Request.Headers, httpContext.RequestAborted)
            .ConfigureAwait(false);

        logger.LogDebug(
            "Database context resolved. Method={Method} Path={Path} Database={Database}",
            httpContext.Request.Method,
            httpContext.Request.Path.Value,
            databaseContext.DatabaseName);

        using var scope = contextAccessor.BeginScope(databaseContext);
        await next().ConfigureAwait(false);
    }
    catch (DatabaseAuthenticationException ex)
    {
        logger.LogWarning(
            ex,
            "Database authentication failed. Method={Method} Path={Path}",
            httpContext.Request.Method,
            httpContext.Request.Path.Value);

        httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await httpContext.Response
            .WriteAsJsonAsync(new { Error = ex.Message }, httpContext.RequestAborted)
            .ConfigureAwait(false);
    }
    catch (ArgumentException ex)
    {
        logger.LogWarning(
            ex,
            "Database context resolution rejected request. Method={Method} Path={Path}",
            httpContext.Request.Method,
            httpContext.Request.Path.Value);

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response
            .WriteAsJsonAsync(new { Error = ex.Message }, httpContext.RequestAborted)
            .ConfigureAwait(false);
    }
});

app.MapControllers();

app.Run();
