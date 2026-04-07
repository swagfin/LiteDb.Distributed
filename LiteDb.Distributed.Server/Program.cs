using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Context;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:1446");
string[] studioCorsOrigins = builder.Configuration.GetSection("Studio:CorsOrigins").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddControllers() .AddJsonOptions(options => { options.JsonSerializerOptions.PropertyNamingPolicy = null; });

builder.Services.ConfigureHttpJsonOptions(options => { options.SerializerOptions.PropertyNamingPolicy = null; });

builder.Services.AddCors(options => { options.AddPolicy("StudioCors", policy => { if (studioCorsOrigins.Length > 0)
        {
            policy.WithOrigins(studioCorsOrigins);
        }
        else
        {
            policy.SetIsOriginAllowed(origin => { if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
                {
                    return false;
                }

                return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
            });
        }

        policy.AllowAnyHeader();
        policy.AllowAnyMethod();
    });
});

ClusterNodeOptions nodeOptions = new ClusterNodeOptions
{
    NodeId = builder.Configuration["Node:NodeId"] ?? "node-1",
    ReplicationApiKey = builder.Configuration["Node:ReplicationApiKey"] ?? "I_AM_ONE_OF_YOU",
    ReplicationBatchSize = builder.Configuration.GetValue<int?>("Node:ReplicationBatchSize") ?? 1000,
    ReplicationPeerConcurrency = builder.Configuration.GetValue<int?>("Node:ReplicationPeerConcurrency") ?? 4,
    CacheCleanupIntervalSeconds = builder.Configuration.GetValue<int?>("Node:CacheCleanupIntervalSeconds") ?? 30,
    CacheCleanupBatchSize = builder.Configuration.GetValue<int?>("Node:CacheCleanupBatchSize") ?? 500,
    CacheCleanupMaxScanPages = builder.Configuration.GetValue<int?>("Node:CacheCleanupMaxScanPages") ?? 20,
    CriticalCollections = builder.Configuration.GetSection("Node:CriticalCollections").Get<string[]>() ?? Array.Empty<string>(),
    SeedPeers = builder.Configuration.GetSection("Node:SeedPeers").Get<ClusterPeer[]>() ?? Array.Empty<ClusterPeer>()
};

builder.Services.AddLiteDbDistributedNode(nodeOptions);

builder.Services.AddSingleton(sp =>
{
    ApiKeyAuthorizationOptions authOptions = new ApiKeyAuthorizationOptions();
    builder.Configuration.GetSection("Auth").Bind(authOptions);
    return authOptions;
});
builder.Services.AddSingleton<IApiKeyAuthorizationService, ApiKeyAuthorizationService>();

WebApplication app = builder.Build();
ILogger logger = app.Logger;

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("StudioCors");

app.Use(async (httpContext, next) =>
{
    bool isReplicationApiPath = httpContext.Request.Path.StartsWithSegments("/api/replication", StringComparison.OrdinalIgnoreCase);
    bool isClusterApiPath = httpContext.Request.Path.StartsWithSegments("/api/cluster", StringComparison.OrdinalIgnoreCase);
    bool isReplicationWebSocketPath = httpContext.Request.Path.StartsWithSegments("/ws/replication", StringComparison.OrdinalIgnoreCase);
    if (!isReplicationApiPath && !isClusterApiPath && !isReplicationWebSocketPath)
    {
        await next().ConfigureAwait(false);
        return;
    }

    string providedReplicationApiKey = httpContext.Request.Headers["ReplicationApiKey"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(providedReplicationApiKey) || !string.Equals(providedReplicationApiKey, nodeOptions.ReplicationApiKey, StringComparison.Ordinal))
    {
        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        await httpContext.Response.WriteAsJsonAsync(new { Error = "ReplicationApiKey is invalid." }, httpContext.RequestAborted).ConfigureAwait(false);
        return;
    }

    if (!isReplicationApiPath)
    {
        await next().ConfigureAwait(false);
        return;
    }

    try
    {
        string rawDatabaseName = httpContext.Request.Headers["Database"].ToString();
        string normalizedDatabaseName = DatabaseNameNormalizer.Normalize(rawDatabaseName);
        IDatabaseContextAccessor contextAccessor = httpContext.RequestServices.GetRequiredService<IDatabaseContextAccessor>();

        using IDisposable scope = contextAccessor.BeginScope(new DatabaseRequestContext
        {
            DatabaseName = normalizedDatabaseName,
            ApiKey = nodeOptions.ReplicationApiKey,
            IsRoot = true,
            CanAddDatabase = true,
            CanDeleteDatabase = true,
            CanReadDocument = true,
            CanWriteDocument = true,
            CanUpdateDocument = true,
            CanDeleteDocument = true
        });

        await next().ConfigureAwait(false);
    }
    catch (ArgumentException ex)
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(new { Error = ex.Message }, httpContext.RequestAborted).ConfigureAwait(false);
    }
});

app.Use(async (httpContext, next) =>
{
    bool isApiPath = httpContext.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
    bool isReplicationApiPath = httpContext.Request.Path.StartsWithSegments("/api/replication", StringComparison.OrdinalIgnoreCase);
    bool isClusterApiPath = httpContext.Request.Path.StartsWithSegments("/api/cluster", StringComparison.OrdinalIgnoreCase);
    if (!isApiPath || isReplicationApiPath || isClusterApiPath)
    {
        await next().ConfigureAwait(false);
        return;
    }

    IDatabaseRequestContextResolver contextResolver = httpContext.RequestServices.GetRequiredService<IDatabaseRequestContextResolver>();
    IDatabaseContextAccessor contextAccessor = httpContext.RequestServices.GetRequiredService<IDatabaseContextAccessor>();

    try
    {
        logger.LogDebug("Resolving database context for request. Method={Method} Path={Path}", httpContext.Request.Method, httpContext.Request.Path.Value);

        DatabaseRequestContext databaseContext = await contextResolver.ResolveAsync(httpContext.Request.Headers, httpContext.RequestAborted).ConfigureAwait(false);

        logger.LogDebug("Database context resolved. Method={Method} Path={Path} Database={Database}", httpContext.Request.Method, httpContext.Request.Path.Value, databaseContext.DatabaseName);

        using IDisposable scope = contextAccessor.BeginScope(databaseContext);
        await next().ConfigureAwait(false);
    }
    catch (UnauthorizedAccessException ex)
    {
        logger.LogWarning(ex, "Database authorization failed. Method={Method} Path={Path}", httpContext.Request.Method, httpContext.Request.Path.Value);

        httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        await httpContext.Response.WriteAsJsonAsync(new { Error = ex.Message }, httpContext.RequestAborted).ConfigureAwait(false);
    }
    catch (ArgumentException ex)
    {
        logger.LogWarning(ex, "Database context resolution rejected request. Method={Method} Path={Path}", httpContext.Request.Method, httpContext.Request.Path.Value);

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response
            .WriteAsJsonAsync(new { Error = ex.Message }, httpContext.RequestAborted).ConfigureAwait(false);
    }
});

app.MapControllers();

app.Run();

