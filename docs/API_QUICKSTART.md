# API Quickstart

This guide shows the basic document API flow: choose a logical database, authenticate with an API key, create a collection, write records, read them, update them, and delete them.

LiteDb.Distributed is document-first. What many database products call a table is called a collection here.

## 1. Start A Node

```console
dotnet run --project .\LiteDb.Distributed.Server\LiteDb.Distributed.Server.csproj
```

The examples below assume the node is available at:

```text
http://localhost:17001
```

Change the base URL in the examples if your node is running on another port.

## 2. Pick A Database And API Key

Every document API request must include these headers:

- `Database`: the logical database name
- `ApiKey`: an API key with the required permissions

API keys are configured on the server under `Auth:ApiKeys` in `appsettings.json`. *LiteDb.Distributed does not currently expose an endpoint that generates API keys for you.*

The root key can access every database and can create missing databases. Production should use a non-default root key and named API keys from `Auth:ApiKeys`. See [Configuration](./CONFIGURATION.md).

Example named API key configuration:

```json
{
  "Auth": {
    "ApiKeys": [
      {
        "Name": "orders-api",
        "Key": "replace-with-a-long-random-secret",
        "Databases": [ "orders" ],
        "Roles": {
          "ADD_DB": true,
          "DELETE_DB": false,
          "READ_DOCUMENT": true,
          "WRITE_DOCUMENT": true,
          "UPDATE_DOCUMENT": true,
          "DELETE_DOCUMENT": true
        }
      }
    ]
  }
}
```

`Databases` controls which logical databases the key can access. Use explicit names such as `orders` for normal applications. A wildcard value of `*` gives access to every database and should be reserved for trusted administrative clients.

If the `Database` header names a database that does not exist yet:

- the server creates it automatically when the key has `ADD_DB`
- the server rejects the request when the key does not have `ADD_DB`

## 3. Create A Database

There is no separate create-database endpoint. A logical database is created automatically on the first authenticated request when the key has `ADD_DB` permission.


## 4. List Documents/Tables

This request retrieves all documents in a logical database: Example to retrieve documents under database `orders`:

```bash
curl -X GET "http://localhost:17001/api/documents" \
  -H "Database: orders" \
  -H "ApiKey: root"
```

## 5. Insert Or Replace A Record

Use `PUT /api/documents/{collection}/{id}` to upsert a document.

```bash
curl -X PUT "http://localhost:17001/api/documents/order_transactions/ord-1001" \
  -H "Database: orders" \
  -H "ApiKey: root" \
  -H "Content-Type: application/json" \
  -d '{
    "customer": "Ada Lovelace",
    "status": "Pending",
    "total": 149.95
  }'
```

The route id is the source of truth. The server stores it as the document `Id`.

## 6. Read A Record

```bash
curl -X GET "http://localhost:17001/api/documents/order_transactions/ord-1001" \
  -H "Database: orders" \
  -H "ApiKey: root"
```

## 7. List Records

```bash
curl -X GET "http://localhost:17001/api/documents/order_transactions?skip=0&take=100" \
  -H "Database: orders" \
  -H "ApiKey: root"
```

## 8. Delete A Record

```bash
curl -X DELETE "http://localhost:17001/api/documents/order_transactions/ord-1001" \
  -H "Database: orders" \
  -H "ApiKey: root"
```
