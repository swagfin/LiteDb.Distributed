using DistributedCacheProbe.Support;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;

CacheProbeSettings settings = CacheProbeSettings.Load();
CancellationTokenSource cancellation = new CancellationTokenSource();
CacheProbeMetrics metrics = new CacheProbeMetrics();

Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};

List<CacheProbeNode> nodes = settings.Nodes.Select((baseUrl, index) => new CacheProbeNode($"node-{index + 1}", NormalizeBaseUrl(baseUrl), CreateNodeClient(baseUrl, settings))).ToList();

Console.WriteLine("Distributed Cache Probe");
Console.WriteLine($"Database: {settings.Database}");
Console.WriteLine($"Nodes: {string.Join(", ", nodes.Select(x => $"{x.Name}@{x.BaseUrl}"))}");
Console.WriteLine($"Poll interval: {settings.PollIntervalMilliseconds} ms (measurement floor is approximately this value)");
Console.WriteLine($"Visibility timeout: {settings.VisibilityTimeoutSeconds} seconds");
Console.WriteLine($"TTL range: {settings.MinRandomTtlMinutes}-{settings.MaxRandomTtlMinutes} minutes (random per key)");
Console.WriteLine("Goal: measure how long a written cache key takes to appear on all peer nodes.");
Console.WriteLine("Press Ctrl+C to stop.");

long iteration = 0L;

try
{
    while (!cancellation.Token.IsCancellationRequested)
    {
        iteration++;
        CacheProbeIterationResult result = await RunProbeIterationAsync(iteration, nodes, settings, cancellation.Token).ConfigureAwait(false);
        metrics.Record(result);
        PrintIterationResult(iteration, result);

        int pauseMs = Random.Shared.Next(settings.MinPauseMilliseconds, settings.MaxPauseMilliseconds + 1);
        await SafeDelayAsync(TimeSpan.FromMilliseconds(pauseMs), cancellation.Token).ConfigureAwait(false);
    }
}
finally
{
    foreach (CacheProbeNode node in nodes)
    {
        node.Client.Dispose();
    }

    metrics.PrintFinal();
}

return;

static async Task<CacheProbeIterationResult> RunProbeIterationAsync(long iteration, IReadOnlyList<CacheProbeNode> nodes, CacheProbeSettings settings, CancellationToken cancellationToken)
{
    CacheProbeNode writer = nodes[Random.Shared.Next(0, nodes.Count)];
    string key = $"cache-probe-{iteration:D8}-{Guid.NewGuid():N}";
    string ttl = CreateRandomTtl(settings);
    CacheValue payload = new CacheValue
    {
        Value = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
        OriginNode = writer.Name,
        WrittenUtc = DateTime.UtcNow,
        Ttl = ttl
    };

    bool writeOk = await TryWriteAsync(writer, key, payload, ttl, cancellationToken).ConfigureAwait(false);
    if (!writeOk)
    {
        return new CacheProbeIterationResult
        {
            Key = key,
            WriterNodeName = writer.Name,
            WriterBaseUrl = writer.BaseUrl,
            Ttl = ttl,
            WriteSucceeded = false
        };
    }

    List<CacheProbeNode> peers = nodes.Where(node => !ReferenceEquals(node, writer)).ToList();
    List<Task<ReplicationProbeResult>> probeTasks = peers.Select(node => WaitForReplicationAsync(node, key, settings, cancellationToken)).ToList();
    ReplicationProbeResult[] peerResults = await Task.WhenAll(probeTasks).ConfigureAwait(false);

    return new CacheProbeIterationResult
    {
        Key = key,
        WriterNodeName = writer.Name,
        WriterBaseUrl = writer.BaseUrl,
        Ttl = ttl,
        WriteSucceeded = true,
        PeerResults = peerResults
    };
}

static HttpClient CreateNodeClient(string baseUrl, CacheProbeSettings settings)
{
    HttpClient client = new HttpClient
    {
        BaseAddress = new Uri(NormalizeBaseUrl(baseUrl)),
        Timeout = TimeSpan.FromSeconds(settings.HttpTimeoutSeconds)
    };

    client.DefaultRequestHeaders.Add("Database", settings.Database);
    client.DefaultRequestHeaders.Add("ApiKey", settings.ApiKey);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    return client;
}

static async Task<bool> TryWriteAsync(CacheProbeNode node, string key, CacheValue payload, string ttl, CancellationToken cancellationToken)
{
    string endpoint = $"/api/cache/{Uri.EscapeDataString(key)}?ttl={Uri.EscapeDataString(ttl)}";

    try
    {
        using HttpResponseMessage response = await node.Client.PutAsJsonAsync(endpoint, payload, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Write failed on {node.Name} ({node.BaseUrl}). Status={(int)response.StatusCode} Key={key} Body={body}");
        return false;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.WriteLine($"Write failed on {node.Name} ({node.BaseUrl}) Key={key}. Error={ex.Message}");
        return false;
    }
}

static async Task<ReplicationProbeResult> WaitForReplicationAsync(CacheProbeNode node, string key, CacheProbeSettings settings, CancellationToken cancellationToken)
{
    TimeSpan timeout = TimeSpan.FromSeconds(settings.VisibilityTimeoutSeconds);
    TimeSpan pollDelay = TimeSpan.FromMilliseconds(settings.PollIntervalMilliseconds);
    DateTime deadline = DateTime.UtcNow + timeout;
    Stopwatch stopwatch = Stopwatch.StartNew();

    while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
    {
        bool exists = await CheckKeyExistsAsync(node, key, cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return new ReplicationProbeResult(node.Name, true, stopwatch.Elapsed);
        }

        await SafeDelayAsync(pollDelay, cancellationToken).ConfigureAwait(false);
    }

    return new ReplicationProbeResult(node.Name, false, stopwatch.Elapsed);
}

static async Task<bool> CheckKeyExistsAsync(CacheProbeNode node, string key, CancellationToken cancellationToken)
{
    string endpoint = $"/api/cache/{Uri.EscapeDataString(key)}";

    try
    {
        using HttpResponseMessage response = await node.Client.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.WriteLine($"Read check failed on {node.Name} ({node.BaseUrl}) Key={key}. Error={ex.Message}");
        return false;
    }
}

static void PrintIterationResult(long iteration, CacheProbeIterationResult result)
{
    if (!result.WriteSucceeded)
    {
        Console.WriteLine($"[{iteration:D4}] Write failed for key {result.Key} on {result.WriterNodeName} ({result.WriterBaseUrl})");
        return;
    }

    string allPeerSummary = result.AllPeersVisibleElapsed is TimeSpan elapsed ? $"all peers visible in {elapsed.TotalMilliseconds:F0} ms" : "timed out before all peers saw key";
    Console.WriteLine($"[{iteration:D4}] Wrote key {result.Key} ttl={result.Ttl} on {result.WriterNodeName} ({result.WriterBaseUrl}) -> {allPeerSummary}");

    foreach (ReplicationProbeResult peer in result.PeerResults.OrderBy(x => x.NodeName, StringComparer.Ordinal))
    {
        string peerSummary = peer.Found ? $"found in {peer.Elapsed.TotalMilliseconds:F0} ms" : $"timeout after {peer.Elapsed.TotalMilliseconds:F0} ms";
        Console.WriteLine($"  -> {peer.NodeName}: {peerSummary}");
    }
}

static string CreateRandomTtl(CacheProbeSettings settings)
{
    int ttlMinutes = Random.Shared.Next(settings.MinRandomTtlMinutes, settings.MaxRandomTtlMinutes + 1);
    return $"{ttlMinutes}m";
}

static async Task SafeDelayAsync(TimeSpan duration, CancellationToken cancellationToken)
{
    try
    {
        await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        // Graceful stop.
    }
}

static string NormalizeBaseUrl(string baseUrl)
{
    return baseUrl.TrimEnd('/');
}
