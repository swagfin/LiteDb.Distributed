using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

var settings = CacheProbeSettings.Load();
var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};

var nodes = settings.Nodes
    .Select((baseUrl, index) => new CacheProbeNode(
        Name: $"node-{index + 1}",
        BaseUrl: NormalizeBaseUrl(baseUrl),
        Client: CreateNodeClient(baseUrl, settings)))
    .ToList();

Console.WriteLine("Distributed Cache Probe");
Console.WriteLine($"Database: {settings.Database}");
Console.WriteLine($"Collection: {settings.CollectionName}");
Console.WriteLine($"Nodes: {string.Join(", ", nodes.Select(x => $"{x.Name}@{x.BaseUrl}"))}");
Console.WriteLine("Press Ctrl+C to stop.");

var iteration = 0L;

while (!cancellation.Token.IsCancellationRequested)
{
    iteration++;
    var writer = nodes[Random.Shared.Next(0, nodes.Count)];
    var key = $"cache-{Guid.NewGuid():N}";
    var value = Convert.ToHexString(Guid.NewGuid().ToByteArray());
    var payload = new CacheEntry
    {
        Id = key,
        Value = value,
        OriginNode = writer.Name,
        WrittenUtc = DateTime.UtcNow
    };

    var writeOk = await TryWriteAsync(writer, settings.CollectionName, payload, cancellation.Token).ConfigureAwait(false);
    if (!writeOk)
    {
        await SafeDelayAsync(TimeSpan.FromMilliseconds(500), cancellation.Token).ConfigureAwait(false);
        continue;
    }

    var readers = nodes.Where(node => !ReferenceEquals(node, writer)).ToList();
    var measureTasks = readers
        .Select(node => WaitForReplicationAsync(node, settings.CollectionName, key, settings, cancellation.Token))
        .ToList();

    var results = await Task.WhenAll(measureTasks).ConfigureAwait(false);

    Console.WriteLine($"[{iteration:D4}] Wrote key {key} on {writer.Name} ({writer.BaseUrl})");

    foreach (var result in results.OrderBy(x => x.NodeName, StringComparer.Ordinal))
    {
        if (result.Found)
        {
            Console.WriteLine($"  -> {result.NodeName}: found in {result.Elapsed.TotalMilliseconds:F0} ms");
        }
        else
        {
            Console.WriteLine($"  -> {result.NodeName}: timeout after {result.Elapsed.TotalMilliseconds:F0} ms");
        }
    }

    var pauseMs = Random.Shared.Next(settings.MinPauseMilliseconds, settings.MaxPauseMilliseconds + 1);
    await SafeDelayAsync(TimeSpan.FromMilliseconds(pauseMs), cancellation.Token).ConfigureAwait(false);
}

foreach (var node in nodes)
{
    node.Client.Dispose();
}

return;

static HttpClient CreateNodeClient(string baseUrl, CacheProbeSettings settings)
{
    var client = new HttpClient
    {
        BaseAddress = new Uri(NormalizeBaseUrl(baseUrl)),
        Timeout = TimeSpan.FromSeconds(10)
    };

    client.DefaultRequestHeaders.Add("Database", settings.Database);
    client.DefaultRequestHeaders.Add("ApiKey", settings.ApiKey);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    return client;
}

static async Task<bool> TryWriteAsync(
    CacheProbeNode node,
    string collectionName,
    CacheEntry payload,
    CancellationToken cancellationToken)
{
    var endpoint = $"/api/{collectionName}/{payload.Id}";

    try
    {
        using var response = await node.Client
            .PutAsJsonAsync(endpoint, payload, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine(
            $"Write failed on {node.Name} ({node.BaseUrl}). Status={(int)response.StatusCode} Key={payload.Id} Body={body}");
        return false;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.WriteLine($"Write failed on {node.Name} ({node.BaseUrl}) Key={payload.Id}. Error={ex.Message}");
        return false;
    }
}

static async Task<ReplicationProbeResult> WaitForReplicationAsync(
    CacheProbeNode node,
    string collectionName,
    string key,
    CacheProbeSettings settings,
    CancellationToken cancellationToken)
{
    var timeout = TimeSpan.FromSeconds(settings.VisibilityTimeoutSeconds);
    var pollDelay = TimeSpan.FromMilliseconds(settings.PollIntervalMilliseconds);
    var deadline = DateTime.UtcNow + timeout;
    var stopwatch = Stopwatch.StartNew();

    while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
    {
        var exists = await CheckKeyExistsAsync(node, collectionName, key, cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return new ReplicationProbeResult(node.Name, Found: true, stopwatch.Elapsed);
        }

        await SafeDelayAsync(pollDelay, cancellationToken).ConfigureAwait(false);
    }

    return new ReplicationProbeResult(node.Name, Found: false, stopwatch.Elapsed);
}

static async Task<bool> CheckKeyExistsAsync(
    CacheProbeNode node,
    string collectionName,
    string key,
    CancellationToken cancellationToken)
{
    var endpoint = $"/api/{collectionName}/{key}";

    try
    {
        using var response = await node.Client.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        return false;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.WriteLine($"Read check failed on {node.Name} ({node.BaseUrl}) Key={key}. Error={ex.Message}");
        return false;
    }
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

public sealed record CacheProbeSettings
{
    public string[] Nodes { get; init; } = new[]
    {
        "http://localhost:17001",
        "http://localhost:17002",
        "http://localhost:17003"
    };

    public string Database { get; init; } = "testapp";
    public string ApiKey { get; init; } = "sample-local-key";
    public string CollectionName { get; init; } = "CacheEntries";
    public int PollIntervalMilliseconds { get; init; } = 250;
    public int VisibilityTimeoutSeconds { get; init; } = 20;
    public int MinPauseMilliseconds { get; init; } = 500;
    public int MaxPauseMilliseconds { get; init; } = 1500;

    public static CacheProbeSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sample-settings.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Missing configuration file '{path}'.");
        }

        var settings = JsonSerializer.Deserialize<CacheProbeSettings>(File.ReadAllText(path)) ?? new CacheProbeSettings();

        if (settings.Nodes.Length < 3)
        {
            throw new InvalidOperationException("sample-settings.json must define at least 3 node URLs.");
        }

        if (string.IsNullOrWhiteSpace(settings.Database) || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("sample-settings.json is missing required Database/ApiKey.");
        }

        var normalizedNodes = settings.Nodes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedNodes.Length < 3)
        {
            throw new InvalidOperationException("sample-settings.json must define at least 3 unique node URLs.");
        }

        var pollMs = Math.Max(100, settings.PollIntervalMilliseconds);
        var timeoutSeconds = Math.Max(2, settings.VisibilityTimeoutSeconds);
        var minPauseMs = Math.Max(100, settings.MinPauseMilliseconds);
        var maxPauseMs = Math.Max(minPauseMs, settings.MaxPauseMilliseconds);

        return settings with
        {
            Nodes = normalizedNodes,
            Database = settings.Database.Trim(),
            ApiKey = settings.ApiKey.Trim(),
            CollectionName = string.IsNullOrWhiteSpace(settings.CollectionName) ? "CacheEntries" : settings.CollectionName.Trim(),
            PollIntervalMilliseconds = pollMs,
            VisibilityTimeoutSeconds = timeoutSeconds,
            MinPauseMilliseconds = minPauseMs,
            MaxPauseMilliseconds = maxPauseMs
        };
    }
}

public sealed record CacheEntry
{
    public required string Id { get; init; }
    public required string Value { get; init; }
    public required string OriginNode { get; init; }
    public required DateTime WrittenUtc { get; init; }
}

public sealed record CacheProbeNode(
    string Name,
    string BaseUrl,
    HttpClient Client);

public sealed record ReplicationProbeResult(
    string NodeName,
    bool Found,
    TimeSpan Elapsed);
