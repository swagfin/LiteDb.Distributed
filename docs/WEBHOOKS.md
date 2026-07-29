# Webhook Ingestion

LiteDb.Distributed can ingest external webhook events and turn them into local document writes.

Use this when another system can dispatch Add, Update, and Delete events and you want those changes stored in a LiteDb.Distributed logical database.

## Endpoint

```http
POST /webhook-ingestion/{databaseName}/{apiKey}
```
*Obviously not the best way to pass apikeys but YAY! we shall revisit this*

Readiness check:

```http
GET /webhook-ingestion/{databaseName}/{apiKey}
```
calling this endpoint will confirm endpoint is working

## Authentication

The `{apiKey}` route value is authorized through the same API key system used by the document API.

The key must have access to `{databaseName}`. For Add and Update events it must have document write/update permission. For Delete events it must have document delete permission.

Example development URL:

```text
http://localhost:1446/webhook-ingestion/testapp/root
```

For production, use a long non-default API key and HTTPS. Keep in mind that route keys can appear in proxy/server logs.

## Payload Shape

```json
{
  "webhookId": "9E63A146-0B10-49B3-8D8B-B62E8F30C9CA",
  "webhookName": "litedb-test",
  "eventId": "64F0541B-0811-4DB0-8841-87ED0EBF7FDB",
  "entityName": "Customer",
  "action": "Add",
  "occurredDate": "2026-07-30T00:04:55.5113857+03:00",
  "data": {
    "id": "1d90cb62-0b95-4162-57e2-08deedb50a8b",
    "firstName": "Johnie",
    "lastName": "Doe",
    "email": "johnie.doe@example.com",
    "phoneNumber": "+2547000123456",
    "status": "Active"
  }
}
```

Required fields:

- `entityName`: target collection name.
- `action`: `Add`, `Update`, or `Delete`.
- `data`: JSON object containing the document data.

Optional metadata fields such as `webhookId`, `webhookName`, `eventId`, `occurredDate` etc are accepted for compatibility with webhook providers, but they are not required for storage.

## Collection Mapping

`entityName` becomes the LiteDb.Distributed collection name.

## Primary Key Resolution

The document id is resolved from `data` in this order:

1. `data.Id`
2. `data.id`
3. The first property in `data`

The primary key value must be a string, number, or boolean. Object, array, and null values are rejected as primary keys.

The resolved value becomes the internal document `Id`. The full `data` object is still saved as the document body, with the document `Id` normalized by the existing document write pipeline.

Example with `id`:

```json
{
  "entityName": "Customer",
  "action": "Add",
  "data": {
    "id": "1d90cb62-0b95-4162-57e2-08deedb50a8b",
    "firstName": "Grace",
    "lastName": "Wanjiku"
  }
}
```

## Replication

Webhook-ingested writes use the same local document writer as normal API and Studio writes. That means changes are written locally, operation-log records are created, and peers are signaled for replication.

## Current Limits

- No `eventId` idempotency tracking is performed.
- `primaryKey` metadata is accepted but not currently used for identity resolution.
- The route API key is convenient for webhook providers, but header-based secrets are generally safer when the provider supports them.
