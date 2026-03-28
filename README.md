# LiteDb.Distributed

LiteDb.Distributed is a local-first, eventually consistent distributed document database built on top of LiteDB.

Each node:
- Writes to its own local LiteDB file first.
- Appends an immutable operation log entry for every write.
- Replicates operations (not database files) to peer nodes.
- Replays remote operations into local materialized state.
- Serves reads from local state for offline-friendly behavior.

## What This Project Is

- A practical distributed document database MVP.
- Focused on correctness, simplicity, and extensibility.
- Operation-log replication with peer-to-peer anti-entropy sync.

## What This Project Is Not

- Not a SQL engine.
- Not a SQLite clone.
- Not a file-level replication system.

## Solution Layout

- `LiteDb.Distributed.Core`
Core domain models and abstractions.

- `LiteDb.Distributed.Infrastructure`
LiteDB storage, operation ingestion, conflict handling, peer replication services, and DI wiring.

- `LiteDb.Distributed.Server`
ASP.NET Core node host with document and replication endpoints.

- `LiteDb.Distributed.Tests`
Automated tests for cross-node replication behavior.

- `Samples/SaveFewRecordsSample`
Small client that writes sample records to a running node.

## Node API (Current)

- `PUT /api/documents/{collection}/{id}`
- `DELETE /api/documents/{collection}/{id}`
- `GET /api/documents/{collection}/{id}`
- `GET /api/documents/{collection}?skip=0&take=100`
- `POST /api/cluster/peers`
- `GET /api/cluster/peers`
- `POST /api/replication/push`
- `POST /api/replication/pull`
- `POST /api/replication/trigger`

## Default Port

Server defaults to:

- `http://localhost:1446`

## Quick Start

1. Run a node:
```powershell
dotnet run --project .\LiteDb.Distributed.Server\LiteDb.Distributed.Server.csproj
```

2. Run sample writes:
```powershell
dotnet run --project .\Samples\SaveFewRecordsSample\SaveFewRecordsSample.csproj
```

3. Optional: run tests:
```powershell
dotnet test .\LiteDb.Distributed.Tests\LiteDb.Distributed.Tests.csproj
```

## Multi-Node Example

Start three nodes on different ports:

```powershell
dotnet run --project .\LiteDb.Distributed.Server\LiteDb.Distributed.Server.csproj --urls http://localhost:7001 --Node:NodeId=node-1 --Node:DatabasePath=Data/node-1.db
dotnet run --project .\LiteDb.Distributed.Server\LiteDb.Distributed.Server.csproj --urls http://localhost:7002 --Node:NodeId=node-2 --Node:DatabasePath=Data/node-2.db
dotnet run --project .\LiteDb.Distributed.Server\LiteDb.Distributed.Server.csproj --urls http://localhost:7003 --Node:NodeId=node-3 --Node:DatabasePath=Data/node-3.db
```

Then register peers (`POST /api/cluster/peers`) on each node, and replication will converge via background sync (or `POST /api/replication/trigger`).

## Current Consistency Model

- Local-first writes (immediate local commit).
- Eventual consistency across nodes.
- Conflict resolution strategy is pluggable.
- Default behavior includes Last-Write-Wins with optional conflict recording for critical collections.

## Roadmap Ideas

- Authenticated inter-node replication.
- Richer peer health and retry/circuit-breaker controls.
- Operational tooling for cluster bootstrap and diagnostics.
