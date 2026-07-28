# Cache API

LiteDb.Distributed includes a replicated cache API for short-lived values. Cache entries are stored in a reserved internal collection and replicated like other writes.

Use this API when you want durable local cache writes that become visible on peer nodes through normal replication.

## Headers

Every cache request needs:

```text
Database: orders
ApiKey: root
Content-Type: application/json
```

The examples below assume the node is available at `http://localhost:17001`.

## Set A Cache Key

```bash
curl -X PUT "http://localhost:17001/api/cache/order-status:ord-1001?ttl=5m" \
  -H "Database: orders" \
  -H "ApiKey: root" \
  -H "Content-Type: application/json" \
  -d '{
    "status": "ready",
    "source": "node-1"
  }'
```

The response includes:

- `key`
- `version`
- `committedUtc`
- `expiresAtUtc`
- `ttl`

## TTL Format

Use the `ttl` query string parameter:

```text
PUT /api/cache/{key}?ttl=5m
```

Supported examples:

- `500ms`
- `30s`
- `5m`
- `2h`
- `1d`

If `ttl` is omitted, the server uses the default TTL of 5 minutes. The maximum TTL is 30 days.

## Get A Cache Key

```bash
curl -X GET "http://localhost:17001/api/cache/order-status:ord-1001" \
  -H "Database: orders" \
  -H "ApiKey: root"
```

If the key exists and has not expired, the response includes:

- `key`
- `value`
- `createdUtc`
- `updatedUtc`
- `expiresAtUtc`
- `remainingTtl`

If the key does not exist or has expired, the server returns `404 Not Found`.

Expired keys are removed lazily when read, and a background sweeper also removes cold expired keys.

## Delete A Cache Key

```bash
curl -X DELETE "http://localhost:17001/api/cache/order-status:ord-1001" \
  -H "Database: orders" \
  -H "ApiKey: root"
```

Deletes are replicated to peers.

## Replication Behavior

Cache writes, lazy expiry deletes, sweeper deletes, and explicit deletes all go through the same local write and replication pipeline as documents.

That means:

- the write is durable locally first
- peers receive it asynchronously
- another node may see the old value until replication catches up
- TTL is based on the writer node's UTC expiry timestamp

For measuring cache replication effectiveness, use:

```console
dotnet run --project .\Samples\DistributedCacheProbe\DistributedCacheProbe.csproj
```

## Key Rules

- keys cannot be empty
- keys cannot exceed 256 characters
- cache values can be any JSON value
- the reserved cache collection is not available through `/api/documents`
