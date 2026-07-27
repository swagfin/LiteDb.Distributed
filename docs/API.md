# API

All normal API requests require:

- `Database`: logical database name
- `ApiKey`: API key with the needed role

Node-to-node endpoints also require:

- `ReplicationApiKey`: shared cluster key configured by `Node:ReplicationApiKey`

## Documents

| Method | Endpoint | Purpose |
| --- | --- | --- |
| `GET` | `/api/documents` | List business collections |
| `GET` | `/api/documents/{collection}` | List documents |
| `GET` | `/api/documents/{collection}/{id}` | Get document |
| `PUT` | `/api/documents/{collection}/{id}` | Upsert document |
| `DELETE` | `/api/documents/{collection}/{id}` | Delete document |

Reserved `_sys_*` collections are internal. The generic documents API also blocks direct access to the reserved cache collection.

## Query

`POST /api/query`

Example body:

```json
{
  "query": "SELECT $ FROM OrderTransactions LIMIT 100",
  "take": 100
}
```

Supported statements:

- `SELECT`
- `INSERT`
- `UPDATE`
- `DELETE`

Write queries are routed through the document writer pipeline, so operation-log append and replication signaling still happen.

## Cache

Each logical database includes a reserved replicated cache collection named `cache`.

| Method | Endpoint | Purpose |
| --- | --- | --- |
| `PUT` | `/api/cache/{key}?ttl=5m` | Set cache value |
| `GET` | `/api/cache/{key}` | Get cache value |
| `DELETE` | `/api/cache/{key}` | Delete cache value |

TTL examples:

- `30s`
- `5m`
- `2h`
- `1d`

## Cluster And Replication

| Method | Endpoint | Purpose |
| --- | --- | --- |
| `POST` | `/api/cluster/peers` | Register/update peer |
| `GET` | `/api/cluster/peers` | List peers |
| `POST` | `/api/replication/push` | Push operations to peer |
| `POST` | `/api/replication/pull` | Pull operations from peer |
| `GET` | `/api/replication/status` | Replication health and lag |
| `POST` | `/api/replication/operation-log/prune` | Trigger operation-log pruning |
| `GET` | `/ws/replication` | WebSocket sync hint channel |
