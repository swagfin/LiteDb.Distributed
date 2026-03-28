using System.Text.Json;
using LiteDb.Distributed.Core.Abstractions;
using LiteDb.Distributed.Core.Collections;
using LiteDb.Distributed.Core.Exceptions;
using LiteDb.Distributed.Core.Models;
using LiteDb.Distributed.Infrastructure.Storage.Internal;
using LiteDB;
using SystemTextJsonSerializer = System.Text.Json.JsonSerializer;

namespace LiteDb.Distributed.Infrastructure.Storage;

public sealed class LiteDbNodeStore :
    ILocalDocumentWriter,
    ILocalDocumentReader,
    IDocumentStateReader,
    IOperationLogStore,
    IConflictStore,
    IRemoteOperationApplier,
    IPeerCheckpointStore,
    IClusterPeerRegistry,
    IDisposable
{
    private const string VersionField = "_sys_version";
    private const string DeletedField = "_sys_deleted";
    private const string TombstoneField = "_sys_tombstone";
    private const string LastWriterNodeIdField = "_sys_last_writer_node_id";
    private const string LastModifiedUtcField = "_sys_last_modified_utc";
    private const string PeerCheckpointsCollectionName = "_sys_peer_checkpoints";
    private const string ClusterPeersCollectionName = "_sys_cluster_peers";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly LiteDatabase _businessDatabase;
    private readonly LiteDatabase _metadataDatabase;
    private readonly string _nodeId;
    private readonly object _gate = new();

    public LiteDbNodeStore(LiteDbNodeStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.NodeId))
        {
            throw new ArgumentException("NodeId is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.DatabaseName))
        {
            throw new ArgumentException("DatabaseName is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.BusinessDatabasePath))
        {
            throw new ArgumentException("BusinessDatabasePath is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.MetadataDatabasePath))
        {
            throw new ArgumentException("MetadataDatabasePath is required.", nameof(options));
        }

        _nodeId = options.NodeId.Trim();
        var businessFullPath = Path.GetFullPath(options.BusinessDatabasePath);
        var metadataFullPath = Path.GetFullPath(options.MetadataDatabasePath);

        EnsureParentDirectory(businessFullPath);
        EnsureParentDirectory(metadataFullPath);

        _businessDatabase = OpenDatabase(
            businessFullPath,
            options.DatabaseName,
            _nodeId,
            logicalFileKind: "business");
        _metadataDatabase = OpenDatabase(
            metadataFullPath,
            options.DatabaseName,
            _nodeId,
            logicalFileKind: "metadata");

        EnsureSystemIndexes();
        SeedPeers(options.SeedPeers);
    }

    public Task<WriteResult> UpsertAsync<TDocument>(
        string collection,
        string entityId,
        TDocument document,
        string? parentVersion = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBusinessCollection(collection);

        if (string.IsNullOrWhiteSpace(entityId))
        {
            throw new ArgumentException("EntityId is required.", nameof(entityId));
        }

        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        var payload = SystemTextJsonSerializer.Serialize(document, JsonOptions);
        var payloadDocument = ParsePayloadAsDocument(payload);
        var committedUtc = DateTime.UtcNow;
        var operationId = Guid.NewGuid().ToString("N");

        lock (_gate)
        {
            BeginCombinedTransaction();

            try
            {
                var sequence = ReserveNextLocalSequence(committedUtc);
                var businessCollection = BusinessCollection(collection);
                var existing = businessCollection.FindById(entityId);

                ValidateParentVersion(parentVersion, existing, collection, entityId);

                var operationType = existing is null || ReadBoolean(existing, DeletedField)
                    ? OperationType.Insert
                    : OperationType.Update;

                var materialized = existing ?? new BsonDocument();
                ReplacePayload(materialized, payloadDocument, entityId);
                ApplySystemMetadata(
                    materialized,
                    version: operationId,
                    isDeleted: false,
                    isTombstone: false,
                    lastWriterNodeId: _nodeId,
                    modifiedUtc: committedUtc);

                businessCollection.Upsert(materialized);

                var operation = new OperationRecord
                {
                    Id = operationId,
                    NodeId = _nodeId,
                    TimestampUtc = committedUtc,
                    Collection = collection,
                    EntityId = entityId,
                    OperationType = operationType,
                    Payload = payload,
                    Sequence = sequence,
                    LogSequence = sequence,
                    ParentVersion = parentVersion,
                    GlobalSequence = null,
                    IsSynced = false,
                    IsTombstone = false
                };

                InsertOperationInternal(operation);
                CommitCombinedTransaction();

                return Task.FromResult(new WriteResult
                {
                    Collection = collection,
                    EntityId = entityId,
                    Version = operationId,
                    CommittedUtc = committedUtc,
                    IsDeleted = false,
                    Operation = operation
                });
            }
            catch
            {
                RollbackCombinedTransaction();
                throw;
            }
        }
    }

    public Task<WriteResult> DeleteAsync(
        string collection,
        string entityId,
        string? parentVersion = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBusinessCollection(collection);

        if (string.IsNullOrWhiteSpace(entityId))
        {
            throw new ArgumentException("EntityId is required.", nameof(entityId));
        }

        var committedUtc = DateTime.UtcNow;
        var operationId = Guid.NewGuid().ToString("N");

        lock (_gate)
        {
            BeginCombinedTransaction();

            try
            {
                var sequence = ReserveNextLocalSequence(committedUtc);
                var businessCollection = BusinessCollection(collection);
                var existing = businessCollection.FindById(entityId);

                ValidateParentVersion(parentVersion, existing, collection, entityId);

                var payload = existing is null
                    ? "{}"
                    : SerializePayloadOnly(existing);

                var tombstone = existing ?? new BsonDocument();
                ClearBusinessFields(tombstone);
                tombstone["_id"] = entityId;
                ApplySystemMetadata(
                    tombstone,
                    version: operationId,
                    isDeleted: true,
                    isTombstone: true,
                    lastWriterNodeId: _nodeId,
                    modifiedUtc: committedUtc);

                businessCollection.Upsert(tombstone);

                var operation = new OperationRecord
                {
                    Id = operationId,
                    NodeId = _nodeId,
                    TimestampUtc = committedUtc,
                    Collection = collection,
                    EntityId = entityId,
                    OperationType = OperationType.Delete,
                    Payload = payload,
                    Sequence = sequence,
                    LogSequence = sequence,
                    ParentVersion = parentVersion,
                    GlobalSequence = null,
                    IsSynced = false,
                    IsTombstone = true
                };

                InsertOperationInternal(operation);
                CommitCombinedTransaction();

                return Task.FromResult(new WriteResult
                {
                    Collection = collection,
                    EntityId = entityId,
                    Version = operationId,
                    CommittedUtc = committedUtc,
                    IsDeleted = true,
                    Operation = operation
                });
            }
            catch
            {
                RollbackCombinedTransaction();
                throw;
            }
        }
    }

    public Task<TDocument?> GetByIdAsync<TDocument>(
        string collection,
        string entityId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBusinessCollection(collection);

        if (string.IsNullOrWhiteSpace(entityId))
        {
            throw new ArgumentException("EntityId is required.", nameof(entityId));
        }

        lock (_gate)
        {
            var materialized = BusinessCollection(collection).FindById(entityId);

            if (materialized is null || ReadBoolean(materialized, DeletedField))
            {
                return Task.FromResult<TDocument?>(default);
            }

            var json = SerializePayloadOnly(materialized);
            var result = SystemTextJsonSerializer.Deserialize<TDocument>(json, JsonOptions);
            return Task.FromResult<TDocument?>(result);
        }
    }

    public Task<IReadOnlyList<TDocument>> ListAsync<TDocument>(
        string collection,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBusinessCollection(collection);

        if (take <= 0)
        {
            return Task.FromResult<IReadOnlyList<TDocument>>(Array.Empty<TDocument>());
        }

        var safeSkip = Math.Max(skip, 0);
        var safeTake = Math.Clamp(take, 1, 10_000);

        lock (_gate)
        {
            var materialized = BusinessCollection(collection)
                .FindAll()
                .Where(d => !ReadBoolean(d, DeletedField))
                .Skip(safeSkip)
                .Take(safeTake)
                .ToList();

            var result = new List<TDocument>(materialized.Count);

            foreach (var bsonDocument in materialized)
            {
                var json = SerializePayloadOnly(bsonDocument);
                var item = SystemTextJsonSerializer.Deserialize<TDocument>(json, JsonOptions);

                if (item is not null)
                {
                    result.Add(item);
                }
            }

            return Task.FromResult<IReadOnlyList<TDocument>>(result);
        }
    }

    public Task<DocumentState?> GetStateAsync(
        string collection,
        string entityId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateBusinessCollection(collection);

        if (string.IsNullOrWhiteSpace(entityId))
        {
            throw new ArgumentException("EntityId is required.", nameof(entityId));
        }

        lock (_gate)
        {
            var materialized = BusinessCollection(collection).FindById(entityId);

            if (materialized is null)
            {
                return Task.FromResult<DocumentState?>(null);
            }

            var state = new DocumentState
            {
                Collection = collection,
                EntityId = entityId,
                Version = ReadString(materialized, VersionField),
                LastWriterNodeId = ReadString(materialized, LastWriterNodeIdField),
                LastModifiedUtc = ReadDateTime(materialized, LastModifiedUtcField),
                IsDeleted = ReadBoolean(materialized, DeletedField),
                Payload = SerializePayloadOnly(materialized)
            };

            return Task.FromResult<DocumentState?>(state);
        }
    }

    public Task AppendOperationAsync(OperationRecord operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOperation(operation);

        lock (_gate)
        {
            if (ContainsOperationInternal(operation.Id))
            {
                return Task.CompletedTask;
            }

            BeginCombinedTransaction();

            try
            {
                var logSequence = operation.LogSequence > 0
                    ? operation.LogSequence
                    : ReserveNextLocalSequence(DateTime.UtcNow);

                InsertOperationInternal(operation with { LogSequence = logSequence });
                CommitCombinedTransaction();
                return Task.CompletedTask;
            }
            catch
            {
                RollbackCombinedTransaction();
                throw;
            }
        }
    }

    public Task<IReadOnlyList<OperationRecord>> GetOperationsAfterLogSequenceAsync(
        long afterLogSequence,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (batchSize <= 0)
        {
            return Task.FromResult<IReadOnlyList<OperationRecord>>(Array.Empty<OperationRecord>());
        }

        var cappedBatchSize = Math.Clamp(batchSize, 1, 10_000);

        lock (_gate)
        {
            var operations = OperationsCollection()
                .Query()
                .Where(x => x.LogSequence > afterLogSequence)
                .OrderBy(x => x.LogSequence)
                .Limit(cappedBatchSize)
                .ToList()
                .Select(MapToOperationRecord)
                .ToList();

            return Task.FromResult<IReadOnlyList<OperationRecord>>(operations);
        }
    }

    public Task<IReadOnlyList<OperationRecord>> GetLocalOperationsAfterSequenceAsync(
        string nodeId,
        long afterSequence,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(nodeId))
        {
            throw new ArgumentException("NodeId is required.", nameof(nodeId));
        }

        if (batchSize <= 0)
        {
            return Task.FromResult<IReadOnlyList<OperationRecord>>(Array.Empty<OperationRecord>());
        }

        var cappedBatchSize = Math.Clamp(batchSize, 1, 10_000);

        lock (_gate)
        {
            var operations = OperationsCollection()
                .Query()
                .Where(x => x.NodeId == nodeId && x.Sequence > afterSequence)
                .OrderBy(x => x.Sequence)
                .Limit(cappedBatchSize)
                .ToList()
                .Select(MapToOperationRecord)
                .ToList();

            return Task.FromResult<IReadOnlyList<OperationRecord>>(operations);
        }
    }

    public Task<bool> ContainsOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException("Operation id is required.", nameof(operationId));
        }

        lock (_gate)
        {
            return Task.FromResult(ContainsOperationInternal(operationId));
        }
    }

    public Task<PeerCheckpointRecord> GetOrCreatePeerCheckpointAsync(
        string localNodeId,
        string peerNodeId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(localNodeId))
        {
            throw new ArgumentException("Local node id is required.", nameof(localNodeId));
        }

        if (string.IsNullOrWhiteSpace(peerNodeId))
        {
            throw new ArgumentException("Peer node id is required.", nameof(peerNodeId));
        }

        lock (_gate)
        {
            var key = BuildPeerCheckpointKey(localNodeId, peerNodeId);
            var checkpoints = PeerCheckpointsCollection();
            var existing = checkpoints.FindById(key);

            if (existing is null)
            {
                existing = new PeerCheckpointEntity
                {
                    Id = key,
                    LocalNodeId = localNodeId,
                    PeerNodeId = peerNodeId,
                    LastPushedLocalLogSequence = 0,
                    LastPulledPeerLogSequence = 0,
                    UpdatedUtc = DateTime.UtcNow
                };

                checkpoints.Insert(existing);
            }

            return Task.FromResult(MapToPeerCheckpointRecord(existing));
        }
    }

    public Task SavePeerCheckpointAsync(
        PeerCheckpointRecord checkpoint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(checkpoint);

        lock (_gate)
        {
            PeerCheckpointsCollection().Upsert(new PeerCheckpointEntity
            {
                Id = BuildPeerCheckpointKey(checkpoint.LocalNodeId, checkpoint.PeerNodeId),
                LocalNodeId = checkpoint.LocalNodeId,
                PeerNodeId = checkpoint.PeerNodeId,
                LastPushedLocalLogSequence = checkpoint.LastPushedLocalLogSequence,
                LastPulledPeerLogSequence = checkpoint.LastPulledPeerLogSequence,
                UpdatedUtc = checkpoint.UpdatedUtc
            });
        }

        return Task.CompletedTask;
    }

    public Task RecordConflictAsync(ConflictRecord conflict, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(conflict);

        lock (_gate)
        {
            ConflictsCollection().Insert(new ConflictEntity
            {
                Id = conflict.Id,
                NodeId = conflict.NodeId,
                Collection = conflict.Collection,
                EntityId = conflict.EntityId,
                IncomingOperationId = conflict.IncomingOperationId,
                LocalVersion = conflict.LocalVersion,
                IncomingVersionHint = conflict.IncomingVersionHint,
                Reason = conflict.Reason,
                CreatedUtc = conflict.CreatedUtc,
                LocalPayload = conflict.LocalPayload,
                IncomingPayload = conflict.IncomingPayload
            });
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClusterPeer>> GetPeersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var peers = ClusterPeersCollection()
                .FindAll()
                .Select(MapToClusterPeer)
                .OrderBy(x => x.NodeId, StringComparer.Ordinal)
                .ToList();

            return Task.FromResult<IReadOnlyList<ClusterPeer>>(peers);
        }
    }

    public Task UpsertPeerAsync(ClusterPeer peer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(peer);

        if (string.IsNullOrWhiteSpace(peer.NodeId))
        {
            throw new ArgumentException("Peer node id is required.", nameof(peer));
        }

        if (string.IsNullOrWhiteSpace(peer.BaseUrl))
        {
            throw new ArgumentException("Peer base URL is required.", nameof(peer));
        }

        lock (_gate)
        {
            ClusterPeersCollection().Upsert(new ClusterPeerEntity
            {
                NodeId = peer.NodeId,
                BaseUrl = NormalizeBaseUrl(peer.BaseUrl),
                IsActive = peer.IsActive,
                UpdatedUtc = peer.UpdatedUtc == default ? DateTime.UtcNow : peer.UpdatedUtc
            });
        }

        return Task.CompletedTask;
    }

    public Task<bool> ApplyRemoteOperationAsync(
        OperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOperation(operation);
        ValidateBusinessCollection(operation.Collection);

        lock (_gate)
        {
            if (ContainsOperationInternal(operation.Id))
            {
                return Task.FromResult(false);
            }

            BeginCombinedTransaction();

            try
            {
                var businessCollection = BusinessCollection(operation.Collection);
                var existing = businessCollection.FindById(operation.EntityId);

                if (operation.OperationType == OperationType.Delete || operation.IsTombstone)
                {
                    var tombstone = existing ?? new BsonDocument();
                    ClearBusinessFields(tombstone);
                    tombstone["_id"] = operation.EntityId;

                    ApplySystemMetadata(
                        tombstone,
                        version: operation.Id,
                        isDeleted: true,
                        isTombstone: true,
                        lastWriterNodeId: operation.NodeId,
                        modifiedUtc: operation.TimestampUtc);

                    businessCollection.Upsert(tombstone);
                }
                else
                {
                    var payload = ParsePayloadAsDocument(operation.Payload);
                    var materialized = existing ?? new BsonDocument();

                    ReplacePayload(materialized, payload, operation.EntityId);
                    ApplySystemMetadata(
                        materialized,
                        version: operation.Id,
                        isDeleted: false,
                        isTombstone: false,
                        lastWriterNodeId: operation.NodeId,
                        modifiedUtc: operation.TimestampUtc);

                    businessCollection.Upsert(materialized);
                }

                var localLogSequence = ReserveNextLocalSequence(DateTime.UtcNow);
                InsertOperationInternal(operation with
                {
                    LogSequence = localLogSequence,
                    IsSynced = true
                });
                CommitCombinedTransaction();

                return Task.FromResult(true);
            }
            catch
            {
                RollbackCombinedTransaction();
                throw;
            }
        }
    }

    public Task<IReadOnlyList<string>> GetBusinessCollectionNamesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var names = _businessDatabase
                .GetCollectionNames()
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            return Task.FromResult<IReadOnlyList<string>>(names);
        }
    }

    public Task<IReadOnlyList<string>> GetMetadataCollectionNamesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var names = _metadataDatabase
                .GetCollectionNames()
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            return Task.FromResult<IReadOnlyList<string>>(names);
        }
    }

    public void Dispose()
    {
        _businessDatabase.Dispose();
        _metadataDatabase.Dispose();
    }

    private static void EnsureParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static LiteDatabase OpenDatabase(
        string fullPath,
        string databaseName,
        string nodeId,
        string logicalFileKind)
    {
        try
        {
            var connectionString = new ConnectionString
            {
                Filename = fullPath
            };

            return new LiteDatabase(connectionString);
        }
        catch (LiteException ex)
        {
            throw new InvalidOperationException(
                $"Failed to open {logicalFileKind} LiteDB file for NodeId='{nodeId}', Database='{databaseName}', Path='{fullPath}'. " +
                "If this file was created when encryption was enabled, delete/recreate the file or migrate it before running this node.",
                ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Failed to open {logicalFileKind} LiteDB file for NodeId='{nodeId}', Database='{databaseName}', Path='{fullPath}'. " +
                "The file is locked by another process. Ensure only one process owns this node/data path and avoid duplicate node instances.",
                ex);
        }
    }

    private void BeginCombinedTransaction()
    {
        _metadataDatabase.BeginTrans();
        _businessDatabase.BeginTrans();
    }

    private void CommitCombinedTransaction()
    {
        // TODO: Harden with compensation/recovery for crash windows between metadata and business commits.
        _metadataDatabase.Commit();
        _businessDatabase.Commit();
    }

    private void RollbackCombinedTransaction()
    {
        TryRollback(_businessDatabase);
        TryRollback(_metadataDatabase);
    }

    private static void TryRollback(LiteDatabase database)
    {
        try
        {
            database.Rollback();
        }
        catch
        {
            // Best effort rollback.
        }
    }

    private void EnsureSystemIndexes()
    {
        var operations = OperationsCollection();
        operations.EnsureIndex(x => x.NodeId);
        operations.EnsureIndex(x => x.Sequence);
        operations.EnsureIndex(x => x.LogSequence);
        operations.EnsureIndex(x => x.TimestampUtc);

        NodeMetadataCollection().EnsureIndex(x => x.NodeId, unique: true);
        ConflictsCollection().EnsureIndex(x => x.CreatedUtc);
        PeerCheckpointsCollection().EnsureIndex(x => x.Id, unique: true);
        ClusterPeersCollection().EnsureIndex(x => x.NodeId, unique: true);
    }

    private ILiteCollection<BsonDocument> BusinessCollection(string collectionName)
    {
        return _businessDatabase.GetCollection<BsonDocument>(collectionName);
    }

    private ILiteCollection<OperationEntity> OperationsCollection()
    {
        return _metadataDatabase.GetCollection<OperationEntity>(SystemCollections.Operations);
    }

    private ILiteCollection<NodeMetadataEntity> NodeMetadataCollection()
    {
        return _metadataDatabase.GetCollection<NodeMetadataEntity>(SystemCollections.NodeMetadata);
    }

    private ILiteCollection<ConflictEntity> ConflictsCollection()
    {
        return _metadataDatabase.GetCollection<ConflictEntity>(SystemCollections.Conflicts);
    }

    private ILiteCollection<PeerCheckpointEntity> PeerCheckpointsCollection()
    {
        return _metadataDatabase.GetCollection<PeerCheckpointEntity>(PeerCheckpointsCollectionName);
    }

    private ILiteCollection<ClusterPeerEntity> ClusterPeersCollection()
    {
        return _metadataDatabase.GetCollection<ClusterPeerEntity>(ClusterPeersCollectionName);
    }

    private long ReserveNextLocalSequence(DateTime writeUtc)
    {
        var nodeMetadata = NodeMetadataCollection();
        var existing = nodeMetadata.FindById(_nodeId) ?? new NodeMetadataEntity
        {
            NodeId = _nodeId,
            LastLocalSequence = 0,
            LastWriteUtc = writeUtc
        };

        existing.LastLocalSequence += 1;
        existing.LastWriteUtc = writeUtc;
        nodeMetadata.Upsert(existing);

        return existing.LastLocalSequence;
    }

    private void UpdateNodeMetadataForRemoteOperation(OperationRecord operation)
    {
        var nodeMetadata = NodeMetadataCollection();
        var existing = nodeMetadata.FindById(operation.NodeId) ?? new NodeMetadataEntity
        {
            NodeId = operation.NodeId,
            LastLocalSequence = 0,
            LastWriteUtc = operation.TimestampUtc
        };

        existing.LastLocalSequence = Math.Max(existing.LastLocalSequence, operation.Sequence);
        existing.LastWriteUtc = existing.LastWriteUtc < operation.TimestampUtc
            ? operation.TimestampUtc
            : existing.LastWriteUtc;

        nodeMetadata.Upsert(existing);
    }

    private static void ValidateParentVersion(
        string? parentVersion,
        BsonDocument? existing,
        string collection,
        string entityId)
    {
        if (string.IsNullOrWhiteSpace(parentVersion))
        {
            return;
        }

        var currentVersion = existing is null
            ? null
            : ReadString(existing, VersionField);

        if (!string.Equals(currentVersion, parentVersion, StringComparison.Ordinal))
        {
            throw new VersionMismatchException(
                $"Version mismatch for {collection}/{entityId}. Expected '{parentVersion}', current is '{currentVersion ?? "<null>"}'.");
        }
    }

    private static void ValidateBusinessCollection(string collection)
    {
        if (string.IsNullOrWhiteSpace(collection))
        {
            throw new ArgumentException("Collection name is required.", nameof(collection));
        }

        if (collection.StartsWith("_sys_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("System collections are internal and cannot be used as business collections.");
        }
    }

    private static void ValidateOperation(OperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (string.IsNullOrWhiteSpace(operation.Id))
        {
            throw new ArgumentException("Operation id is required.", nameof(operation));
        }

        if (string.IsNullOrWhiteSpace(operation.NodeId))
        {
            throw new ArgumentException("Operation node id is required.", nameof(operation));
        }

        if (string.IsNullOrWhiteSpace(operation.Collection))
        {
            throw new ArgumentException("Operation collection is required.", nameof(operation));
        }

        if (string.IsNullOrWhiteSpace(operation.EntityId))
        {
            throw new ArgumentException("Operation entity id is required.", nameof(operation));
        }
    }

    private void InsertOperationInternal(OperationRecord operation)
    {
        OperationsCollection().Insert(MapToOperationEntity(operation));
    }

    private bool ContainsOperationInternal(string operationId)
    {
        return OperationsCollection().FindById(operationId) is not null;
    }

    private static BsonDocument ParsePayloadAsDocument(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new BsonDocument();
        }

        var value = LiteDB.JsonSerializer.Deserialize(payload);

        if (value.IsDocument)
        {
            return value.AsDocument;
        }

        throw new InvalidOperationException("Operation payload must be a JSON document.");
    }

    private static void ReplacePayload(BsonDocument target, BsonDocument payload, string entityId)
    {
        ClearBusinessFields(target);
        target["_id"] = entityId;

        foreach (var entry in payload)
        {
            if (string.Equals(entry.Key, "_id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            target[entry.Key] = entry.Value;
        }
    }

    private static void ApplySystemMetadata(
        BsonDocument target,
        string version,
        bool isDeleted,
        bool isTombstone,
        string lastWriterNodeId,
        DateTime modifiedUtc)
    {
        target[VersionField] = version;
        target[DeletedField] = isDeleted;
        target[TombstoneField] = isTombstone;
        target[LastWriterNodeIdField] = lastWriterNodeId;
        target[LastModifiedUtcField] = modifiedUtc;
    }

    private static void ClearBusinessFields(BsonDocument document)
    {
        var keysToRemove = document.Keys
            .Where(key => !string.Equals(key, "_id", StringComparison.Ordinal) && !key.StartsWith("_sys_", StringComparison.Ordinal))
            .ToList();

        foreach (var key in keysToRemove)
        {
            document.Remove(key);
        }
    }

    private static string SerializePayloadOnly(BsonDocument materialized)
    {
        var payload = new BsonDocument();

        foreach (var entry in materialized)
        {
            if (string.Equals(entry.Key, "_id", StringComparison.Ordinal) || entry.Key.StartsWith("_sys_", StringComparison.Ordinal))
            {
                continue;
            }

            payload[entry.Key] = entry.Value;
        }

        if (!payload.Keys.Any(x => string.Equals(x, "Id", StringComparison.OrdinalIgnoreCase))
            && materialized.TryGetValue("_id", out var idValue)
            && idValue.IsString)
        {
            payload["Id"] = idValue.AsString;
        }

        return LiteDB.JsonSerializer.Serialize(payload);
    }

    private static string ReadString(BsonDocument document, string field)
    {
        return document.TryGetValue(field, out var value) && value.IsString
            ? value.AsString
            : string.Empty;
    }

    private static bool ReadBoolean(BsonDocument document, string field)
    {
        return document.TryGetValue(field, out var value) && value.IsBoolean && value.AsBoolean;
    }

    private static DateTime ReadDateTime(BsonDocument document, string field)
    {
        if (!document.TryGetValue(field, out var value) || !value.IsDateTime)
        {
            return DateTime.MinValue;
        }

        return DateTime.SpecifyKind(value.AsDateTime, DateTimeKind.Utc);
    }

    private static OperationRecord MapToOperationRecord(OperationEntity entity)
    {
        return new OperationRecord
        {
            Id = entity.Id,
            NodeId = entity.NodeId,
            TimestampUtc = entity.TimestampUtc,
            Collection = entity.Collection,
            EntityId = entity.EntityId,
            OperationType = (OperationType)entity.OperationType,
            Payload = entity.Payload,
            Sequence = entity.Sequence,
            LogSequence = entity.LogSequence,
            ParentVersion = entity.ParentVersion,
            GlobalSequence = null,
            IsSynced = entity.IsSynced,
            IsTombstone = entity.IsTombstone
        };
    }

    private static OperationEntity MapToOperationEntity(OperationRecord operation)
    {
        return new OperationEntity
        {
            Id = operation.Id,
            NodeId = operation.NodeId,
            TimestampUtc = operation.TimestampUtc,
            Collection = operation.Collection,
            EntityId = operation.EntityId,
            OperationType = (int)operation.OperationType,
            Payload = operation.Payload,
            Sequence = operation.Sequence,
            LogSequence = operation.LogSequence,
            ParentVersion = operation.ParentVersion,
            IsSynced = operation.IsSynced,
            IsTombstone = operation.IsTombstone
        };
    }

    private void SeedPeers(IReadOnlyList<ClusterPeer> peers)
    {
        if (peers.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            var collection = ClusterPeersCollection();

            foreach (var peer in peers)
            {
                if (string.IsNullOrWhiteSpace(peer.NodeId)
                    || string.IsNullOrWhiteSpace(peer.BaseUrl)
                    || string.Equals(peer.NodeId, _nodeId, StringComparison.Ordinal))
                {
                    continue;
                }

                collection.Upsert(new ClusterPeerEntity
                {
                    NodeId = peer.NodeId,
                    BaseUrl = NormalizeBaseUrl(peer.BaseUrl),
                    IsActive = peer.IsActive,
                    UpdatedUtc = peer.UpdatedUtc == default ? DateTime.UtcNow : peer.UpdatedUtc
                });
            }
        }
    }

    private static PeerCheckpointRecord MapToPeerCheckpointRecord(PeerCheckpointEntity entity)
    {
        return new PeerCheckpointRecord
        {
            LocalNodeId = entity.LocalNodeId,
            PeerNodeId = entity.PeerNodeId,
            LastPushedLocalLogSequence = entity.LastPushedLocalLogSequence,
            LastPulledPeerLogSequence = entity.LastPulledPeerLogSequence,
            UpdatedUtc = entity.UpdatedUtc
        };
    }

    private static ClusterPeer MapToClusterPeer(ClusterPeerEntity entity)
    {
        return new ClusterPeer
        {
            NodeId = entity.NodeId,
            BaseUrl = entity.BaseUrl,
            IsActive = entity.IsActive,
            UpdatedUtc = entity.UpdatedUtc
        };
    }

    private static string BuildPeerCheckpointKey(string localNodeId, string peerNodeId)
    {
        return $"{localNodeId}|{peerNodeId}";
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        return baseUrl.TrimEnd('/');
    }
}

