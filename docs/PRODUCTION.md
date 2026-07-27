# Production Notes

LiteDb.Distributed is production-candidate software for small local-first clusters, branch/edge systems, and durable replicated cache/document workloads.

## Go-Live Checklist

- Replace default `Auth:RootApiKey`.
- Replace default `Node:ReplicationApiKey`.
- Use a stable unique `Node:NodeId` per node.
- Ensure only one process owns each LiteDB database file.
- Configure durable storage for `Node:DataDirectory`.
- Run the samples against a realistic multi-node setup.
- Monitor `/api/replication/status`.
- Decide backup/restore procedure before enabling aggressive pruning.

## Storage Rule

Each logical database is stored as a single LiteDB file per node. Do not point multiple running node processes at the same database file.

## Replication And Pruning

Operation-log pruning is enabled by default. It keeps recent operations and removes older payloads only after active peer checkpoints have moved past them. Compact receipts are retained longer for duplicate suppression.

If a node falls behind the retained log window, replication status reports:

```text
TooOldNeedsSnapshot
```

That node needs a restore/clone workflow before it can safely catch up.

## Operational Signals To Watch

- write latency
- replication pending operations
- peer checkpoint progress
- `TooOldNeedsSnapshot`
- pruning results
- operation receipt growth
- LiteDB file lock/open errors
- cache replication visibility latency

## Load And Soak Testing

Run a three-node cluster, then use:

```powershell
dotnet run --project .\Samples\ClusterSoakTest\ClusterSoakTest.csproj
```

For cache replication visibility:

```powershell
dotnet run --project .\Samples\DistributedCacheProbe\DistributedCacheProbe.csproj
```

Good first target: sustained writes for 15-60 minutes with no replication timeouts and stable pending-operation counts.
