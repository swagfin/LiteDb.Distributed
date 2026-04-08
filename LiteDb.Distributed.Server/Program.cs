using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Context;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:1446");
string[] studioCorsOrigins = builder.Configuration.GetSection("Studio:CorsOrigins").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddControllers().AddJsonOptions(options => { options.JsonSerializerOptions.PropertyNamingPolicy = null; });

builder.Services.ConfigureHttpJsonOptions(options => { options.SerializerOptions.PropertyNamingPolicy = null; });

builder.Services.AddCors(options =>
{
    options.AddPolicy("StudioCors", policy =>
    {
        if (studioCorsOrigins.Length > 0)
        {
            policy.WithOrigins(studioCorsOrigins);
        }
        else
        {
            policy.SetIsOriginAllowed(origin =>
            {
                if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
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
    ConflictResolutionPolicy = builder.Configuration["Node:ConflictResolutionPolicy"] ?? "ApplyIncoming",
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

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("StudioCors");

app.MapControllers();

app.Run();
