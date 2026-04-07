using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Infrastructure.Cache;
using LiteDb.Distributed.Infrastructure.Configuration;
using LiteDb.Distributed.Infrastructure.Conflict;
using LiteDb.Distributed.Infrastructure.Context;
using LiteDb.Distributed.Infrastructure.Replication;
using LiteDb.Distributed.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LiteDb.Distributed.Infrastructure
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddLiteDbDistributedNode(this IServiceCollection services, ClusterNodeOptions options)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(options);

            services.AddSingleton(options);

            services.AddSingleton<IDatabaseContextAccessor, DatabaseContextAccessor>();
            services.AddSingleton<ILogicalDatabaseCatalog, FileLogicalDatabaseCatalog>();
            services.AddSingleton<IDatabaseRequestContextResolver, DatabaseRequestContextResolver>();
            services.AddSingleton<ILogicalDatabaseStoreProvider, LogicalDatabaseStoreProvider>();

            services.AddSingleton<DatabaseScopedNodeStoreAdapter>();
            services.AddSingleton<ILocalDocumentWriter>(sp => sp.GetRequiredService<DatabaseScopedNodeStoreAdapter>());
            services.AddSingleton<ILocalDocumentReader>(sp => sp.GetRequiredService<DatabaseScopedNodeStoreAdapter>());
            services.AddSingleton<IDocumentStateReader>(sp => sp.GetRequiredService<DatabaseScopedNodeStoreAdapter>());
            services.AddSingleton<IOperationLogStore>(sp => sp.GetRequiredService<DatabaseScopedNodeStoreAdapter>());
            services.AddSingleton<IRemoteOperationApplier>(sp => sp.GetRequiredService<DatabaseScopedNodeStoreAdapter>());
            services.AddSingleton<IConflictStore>(sp => sp.GetRequiredService<DatabaseScopedNodeStoreAdapter>());
            services.AddSingleton<IPeerCheckpointStore>(sp => sp.GetRequiredService<DatabaseScopedNodeStoreAdapter>());
            services.AddSingleton<IClusterPeerRegistry>(sp => sp.GetRequiredService<DatabaseScopedNodeStoreAdapter>());

            services.AddHttpClient<IPeerReplicationClient, HttpPeerReplicationClient>();

            services.AddSingleton<IConflictResolver>(sp => new CriticalCollectionConflictResolver(new LastWriteWinsConflictResolver(), options.CriticalCollections));

            services.AddSingleton<IOperationIngestionService, OperationIngestionService>();
            services.AddSingleton<IClusterReplicationService, PeerReplicationService>();
            services.AddSingleton<IReplicationOrchestrator, ReplicationOrchestrator>();
            services.AddSingleton<PeerReplicationSignalService>();
            services.AddSingleton<IReplicationSignalPublisher>(sp => sp.GetRequiredService<PeerReplicationSignalService>());
            services.AddSingleton<IReplicationWebSocketHandler>(sp => sp.GetRequiredService<PeerReplicationSignalService>());
            services.AddHostedService(sp => sp.GetRequiredService<PeerReplicationSignalService>());
            services.AddHostedService<ClusterReplicationBackgroundService>();
            services.AddHostedService<CacheExpirationBackgroundService>();

            return services;
        }
    }

}
