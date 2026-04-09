# Samples

## 1) SaveFewRecordsSample (OrderTransaction Generator)

This sample now runs as a continuous background generator that inserts random `OrderTransaction` records at a random millisecond interval.

- `Samples/SaveFewRecordsSample/sample-settings.json`

Default config:

- `ServerUrl`: `http://localhost:17001`
- `Database`: `testapp`
- `ApiKey`: `root`
- `CollectionName`: `OrderTransactions`
- `MinIntervalMilliseconds`: `0`
- `MaxIntervalMilliseconds`: `1`

Run it with:

```powershell
dotnet run --project .\Samples\SaveFewRecordsSample\SaveFewRecordsSample.csproj
```

Stop it with `Ctrl+C`.

## 2) DistributedCacheProbe (Replication Visibility Probe)

This sample writes random cache keys to random nodes through `/api/cache/{key}` and measures how long those keys take to appear on the other nodes.

Each write uses a random cache `ttl` between `1m` and `3m`.

It reads configuration from:

- `Samples/DistributedCacheProbe/sample-settings.json`

Default config includes:

- 3 node URLs (`17001`, `17002`, `17003`)
- `Database` and `ApiKey`
- polling and timeout settings for replication visibility checks

Run it with:

```powershell
dotnet run --project .\Samples\DistributedCacheProbe\DistributedCacheProbe.csproj
```

Stop it with `Ctrl+C`.
