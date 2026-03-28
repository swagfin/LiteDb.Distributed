# LiteDb.Distributed

LiteDb.Distributed is a local-first, eventually consistent distributed document database built on top of LiteDB.

Each node:
- Writes locally first.
- Appends immutable operation-log entries.
- Replicates operations (not DB files) to peers.
- Replays remote operations into local materialized state.

## Multi-Database Model

This MVP supports multiple logical databases selected from HTTP headers.

Headers required on every `/api/*` request:
- `Database` (required): logical database name.
- `Password` or `ApiKey` (required): validated against the logical database catalog.

Rules:
- Database names are normalized to lowercase (`TestApp` -> `testapp`).
- If a logical database does not exist, it is created automatically.
- If it exists, credentials must match.

Per logical database files:
- Business data: `{AppBaseDirectory}/Data/{nodeId}/{dbName}.db`
- System/replication metadata: `{AppBaseDirectory}/Data/{nodeId}/{dbName}.db.metadata`

Metadata includes operation logs, checkpoints, node metadata, conflicts, sync state, and peer state.

## API (Current)

Document API:
- `GET /api/{documentName}?skip=0&take=50`
- `GET /api/{documentName}/{id}`
- `POST /api/{documentName}`
- `PUT /api/{documentName}/{id}`
- `DELETE /api/{documentName}/{id}`

Replication/cluster API:
- `POST /api/replication/push`
- `POST /api/replication/pull`
- `POST /api/replication/trigger`
- `POST /api/cluster/peers`
- `GET /api/cluster/peers`

Dashboard:
- `GET /` (single-page visibility dashboard)
- `GET /dashboard/api/overview` (dashboard data feed)

Node info:
- `GET /node`

## Solution Layout

- `LiteDb.Distributed.Core`: domain models and abstractions.
- `LiteDb.Distributed.Infrastructure`: storage, replication, conflict handling, DB context resolution.
- `LiteDb.Distributed.Server`: ASP.NET Core node host.
- `LiteDb.Distributed.AppHost`: .NET Aspire host that runs a local 3-node cluster.
- `LiteDb.Distributed.Tests`: replication tests.
- `Samples/SaveFewRecordsSample`: small write/read demo client.

## Default Port

- `http://localhost:1446`

## Quick Start

1. Run a node:
```powershell
dotnet run --project .\LiteDb.Distributed.Server\LiteDb.Distributed.Server.csproj
```

2. Run the sample:
```powershell
dotnet run --project .\Samples\SaveFewRecordsSample\SaveFewRecordsSample.csproj
```

3. Run tests:
```powershell
dotnet test .\LiteDb.Distributed.Tests\LiteDb.Distributed.Tests.csproj
```

## Run 3 Nodes With Aspire

Run all three nodes with one command:

```powershell
dotnet run --project .\LiteDb.Distributed.AppHost\LiteDb.Distributed.AppHost.csproj
```

Configured node URLs:
- `node-1`: `http://localhost:17001`
- `node-2`: `http://localhost:17002`
- `node-3`: `http://localhost:17003`

## Multi-Node Example

```powershell
dotnet run --project .\LiteDb.Distributed.Server\LiteDb.Distributed.Server.csproj --urls http://localhost:7001 --Node:NodeId=node-1
dotnet run --project .\LiteDb.Distributed.Server\LiteDb.Distributed.Server.csproj --urls http://localhost:7002 --Node:NodeId=node-2
dotnet run --project .\LiteDb.Distributed.Server\LiteDb.Distributed.Server.csproj --urls http://localhost:7003 --Node:NodeId=node-3
```

Then register peers per logical database using `POST /api/cluster/peers` with `Database` and `ApiKey` headers.

## Notes

- This is not SQL and does not replicate raw LiteDB files.
- Conflict resolution is pluggable (default includes LWW with optional conflict recording for critical collections).
- Credentials are catalog-based (MVP) and independent of LiteDB file encryption, so resetting a DB credential does not require re-encrypting data files.
