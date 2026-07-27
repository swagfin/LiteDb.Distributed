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

## 3) ClusterSoakTest (Write + Replication Soak Runner)

This sample runs sustained writes across multiple nodes and samples replication visibility on peer nodes. It is intended for load/soak testing with realistic write volume.

It reads configuration from:

- `Samples/ClusterSoakTest/sample-settings.json`

Default config includes:

- 3 node URLs (`17001`, `17002`, `17003`)
- `Database` and `ApiKey`
- `CollectionName`: `LoadOrders`
- `WriterConcurrency`: `16`
- `TargetWritesPerSecond`: `500`
- `ReplicationSampleRate`: `0.02`
- replication polling, timeout, queue, and reporting settings

Run it with:

```powershell
dotnet run --project .\Samples\ClusterSoakTest\ClusterSoakTest.csproj
```

Stop it with `Ctrl+C`, or set `DurationSeconds` in `sample-settings.json`.

Periodic output reports write totals, write RPS, write latency percentiles, replication visibility counts, replication timeouts, dropped replication samples, and replication latency percentiles.
