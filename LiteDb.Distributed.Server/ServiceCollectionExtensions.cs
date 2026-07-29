using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Core.Abstractions;
using LiteDb.Distributed.Server.Core.Context;
using LiteDb.Distributed.Server.Data;
using LiteDb.Distributed.Server.Infrastructure.Cache;
using LiteDb.Distributed.Server.Infrastructure.Conflict;
using LiteDb.Distributed.Server.Infrastructure.Dashboard;
using LiteDb.Distributed.Server.Infrastructure.Replication;

namespace LiteDb.Distributed.Server
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

            services.AddSingleton<IConflictResolver>(sp => new NodeConflictPolicyResolver(options.ConflictResolutionPolicy));

            services.AddSingleton<IOperationIngestionService, OperationIngestionService>();
            services.AddSingleton<IClusterReplicationService, PeerReplicationService>();
            services.AddSingleton<IReplicationStatusService, ReplicationStatusService>();
            services.AddSingleton<DashboardPeerProbeService>();
            services.AddSingleton<OperationLogPruningService>();
            services.AddSingleton<IOperationLogPruningService>(sp => sp.GetRequiredService<OperationLogPruningService>());
            services.AddSingleton<IReplicationOrchestrator, ReplicationOrchestrator>();
            services.AddSingleton<PeerReplicationSignalService>();
            services.AddSingleton<IReplicationSignalPublisher>(sp => sp.GetRequiredService<PeerReplicationSignalService>());
            services.AddSingleton<IReplicationWebSocketHandler>(sp => sp.GetRequiredService<PeerReplicationSignalService>());
            services.AddHostedService(sp => sp.GetRequiredService<PeerReplicationSignalService>());
            services.AddHostedService<ClusterReplicationBackgroundService>();
            services.AddHostedService<CacheExpirationBackgroundService>();
            services.AddHostedService(sp => sp.GetRequiredService<OperationLogPruningService>());

            return services;
        }
    }

}
