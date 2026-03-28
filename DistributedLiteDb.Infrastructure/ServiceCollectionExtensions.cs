using DistributedLiteDb.Core.Abstractions;
using DistributedLiteDb.Infrastructure.Configuration;
using DistributedLiteDb.Infrastructure.Conflict;
using DistributedLiteDb.Infrastructure.Replication;
using DistributedLiteDb.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DistributedLiteDb.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDistributedLiteDbNode(
        this IServiceCollection services,
        ClusterNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);

        services.AddSingleton(sp => new LiteDbNodeStore(new LiteDbNodeStoreOptions
        {
            NodeId = options.NodeId,
            DatabasePath = options.DatabasePath,
            SeedPeers = options.SeedPeers
        }));

        services.AddSingleton<ILocalDocumentWriter>(sp => sp.GetRequiredService<LiteDbNodeStore>());
        services.AddSingleton<ILocalDocumentReader>(sp => sp.GetRequiredService<LiteDbNodeStore>());
        services.AddSingleton<IDocumentStateReader>(sp => sp.GetRequiredService<LiteDbNodeStore>());
        services.AddSingleton<IOperationLogStore>(sp => sp.GetRequiredService<LiteDbNodeStore>());
        services.AddSingleton<IRemoteOperationApplier>(sp => sp.GetRequiredService<LiteDbNodeStore>());
        services.AddSingleton<IConflictStore>(sp => sp.GetRequiredService<LiteDbNodeStore>());
        services.AddSingleton<IPeerCheckpointStore>(sp => sp.GetRequiredService<LiteDbNodeStore>());
        services.AddSingleton<IClusterPeerRegistry>(sp => sp.GetRequiredService<LiteDbNodeStore>());

        services.AddHttpClient<IPeerReplicationClient, HttpPeerReplicationClient>();

        services.AddSingleton<IConflictResolver>(sp =>
            new CriticalCollectionConflictResolver(
                new LastWriteWinsConflictResolver(),
                options.CriticalCollections));

        services.AddSingleton<IOperationIngestionService, OperationIngestionService>();
        services.AddSingleton<IClusterReplicationService, PeerReplicationService>();
        services.AddHostedService<ClusterReplicationBackgroundService>();

        return services;
    }
}
