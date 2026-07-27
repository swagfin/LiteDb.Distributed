# Configuration

## Authentication

Authentication uses server-level API key authorization.

```json
"Auth": {
  "RootApiKey": "root",
  "ApiKeys": [
    {
      "Name": "studio-dev",
      "Key": "dev-123",
      "Databases": [ "testapp", "orders" ],
      "Roles": {
        "ADD_DB": false,
        "DELETE_DB": false,
        "READ_DOCUMENT": true,
        "WRITE_DOCUMENT": true,
        "UPDATE_DOCUMENT": true,
        "DELETE_DOCUMENT": true
      }
    }
  ]
}
```

Roles:

- `ADD_DB`: create missing logical databases
- `DELETE_DB`: delete database
- `READ_DOCUMENT`: read documents and run select queries
- `WRITE_DOCUMENT`: create documents
- `UPDATE_DOCUMENT`: update documents
- `DELETE_DOCUMENT`: delete documents

Production must not use default keys.

## Node Settings

Important settings:

- `Node:NodeId`
- `Node:ReplicationApiKey`
- `Node:ReplicationBatchSize`
- `Node:ReplicationPeerConcurrency`
- `Node:ReplicationSignalAckTimeoutMilliseconds`
- `Node:ConflictResolutionPolicy`
- `Node:OperationLogPruningEnabled`
- `Node:OperationLogRetentionDays`
- `Node:OperationLogRetainRecentOperations`
- `Node:OperationReceiptRetentionDays`
- `Node:CacheCleanupIntervalSeconds`
- `Node:CacheCleanupBatchSize`

## Cache Cleanup

Cache expiration uses:

- read-time lazy expiry
- background sweeper for cold expired keys

Settings:

- `Node:CacheCleanupIntervalSeconds` default `30`
- `Node:CacheCleanupBatchSize` default `500`
- `Node:CacheCleanupMaxScanPages` default `20`
