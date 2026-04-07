using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Models;

namespace LiteDb.Distributed.Infrastructure.Storage
{
    public class DatabaseScopedNodeStoreAdapter :
        ILocalDocumentWriter,
        ILocalDocumentReader,
        IDocumentStateReader,
        IOperationLogStore,
        IConflictStore,
        IRemoteOperationApplier,
        IPeerCheckpointStore,
        IClusterPeerRegistry
    {
        private readonly ILogicalDatabaseStoreProvider _storeProvider;

        public DatabaseScopedNodeStoreAdapter(ILogicalDatabaseStoreProvider storeProvider)
        {
            _storeProvider = storeProvider ?? throw new ArgumentNullException(nameof(storeProvider));
        }

        public async Task<WriteResult> UpsertAsync<TDocument>(string collection, string entityId, TDocument document, string? parentVersion = null, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            return await store.UpsertAsync(collection, entityId, document, parentVersion, cancellationToken).ConfigureAwait(false);
        }

        public async Task<WriteResult> DeleteAsync(string collection, string entityId, string? parentVersion = null, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            return await store.DeleteAsync(collection, entityId, parentVersion, cancellationToken).ConfigureAwait(false);
        }

        public async Task<TDocument?> GetByIdAsync<TDocument>(string collection, string entityId, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            return await store.GetByIdAsync<TDocument>(collection, entityId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<TDocument>> ListAsync<TDocument>(string collection, int skip = 0, int take = 100, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            return await store.ListAsync<TDocument>(collection, skip, take, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<TDocument>> ExecuteQueryAsync<TDocument>(string query, int take = 100, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            return await store.ExecuteQueryAsync<TDocument>(query, take, cancellationToken).ConfigureAwait(false);
        }

        public async Task<DocumentState?> GetStateAsync(string collection, string entityId, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            return await store.GetStateAsync(collection, entityId, cancellationToken).ConfigureAwait(false);
        }

        public async Task AppendOperationAsync(OperationRecord operation, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            await store.AppendOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<OperationRecord>> GetOperationsAfterLogSequenceAsync(long afterLogSequence, int batchSize, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            return await store.GetOperationsAfterLogSequenceAsync(afterLogSequence, batchSize, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<OperationRecord>> GetLocalOperationsAfterSequenceAsync(string nodeId, long afterSequence, int batchSize, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            return await store.GetLocalOperationsAfterSequenceAsync(nodeId, afterSequence, batchSize, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> ContainsOperationAsync(string operationId, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            return await store.ContainsOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<PeerCheckpointRecord> GetOrCreatePeerCheckpointAsync(string localNodeId, string peerNodeId, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            return await store.GetOrCreatePeerCheckpointAsync(localNodeId, peerNodeId, cancellationToken).ConfigureAwait(false);
        }

        public async Task SavePeerCheckpointAsync(PeerCheckpointRecord checkpoint, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            await store.SavePeerCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }

        public async Task RecordConflictAsync(ConflictRecord conflict, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            await store.RecordConflictAsync(conflict, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<ClusterPeer>> GetPeersAsync(CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            return await store.GetPeersAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task UpsertPeerAsync(ClusterPeer peer, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            await store.UpsertPeerAsync(peer, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> ApplyRemoteOperationAsync(OperationRecord operation, CancellationToken cancellationToken = default)
        {
            LiteDbNodeStore store = await _storeProvider.GetCurrentStoreAsync(cancellationToken).ConfigureAwait(false);
            return await store.ApplyRemoteOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        }
    }

}
