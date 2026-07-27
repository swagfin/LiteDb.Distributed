using ClusterSoakTest.Support;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Channels;

SoakTestSettings settings = SoakTestSettings.Load();
CancellationTokenSource cancellation = new CancellationTokenSource();

if (settings.DurationSeconds > 0)
{
    cancellation.CancelAfter(TimeSpan.FromSeconds(settings.DurationSeconds));
}

Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};

List<SoakNode> nodes = settings.Nodes.Select((baseUrl, index) => new SoakNode($"node-{index + 1}", NormalizeBaseUrl(baseUrl), CreateNodeClient(baseUrl, settings))).ToList();
SoakMetrics metrics = new SoakMetrics();
Channel<WrittenDocument> replicationSamples = Channel.CreateBounded<WrittenDocument>(new BoundedChannelOptions(settings.ReplicationQueueCapacity)
{
    FullMode = BoundedChannelFullMode.DropWrite,
    SingleReader = false,
    SingleWriter = false
});

Console.WriteLine("Cluster Soak Test");
Console.WriteLine($"Database: {settings.Database}");
Console.WriteLine($"Collection: {settings.CollectionName}");
Console.WriteLine($"Nodes: {string.Join(", ", nodes.Select(x => $"{x.Name}@{x.BaseUrl}"))}");
Console.WriteLine($"Writer concurrency: {settings.WriterConcurrency}");
Console.WriteLine($"Target write rate: {(settings.TargetWritesPerSecond <= 0 ? "unlimited" : $"{settings.TargetWritesPerSecond} writes/sec")}");
Console.WriteLine($"Replication sample rate: {settings.ReplicationSampleRate:P2}");
Console.WriteLine($"Duration: {(settings.DurationSeconds <= 0 ? "until Ctrl+C" : $"{settings.DurationSeconds} seconds")}");
Console.WriteLine("Press Ctrl+C to stop.");

List<Task> tasks = new List<Task>();

for (int i = 0; i < settings.WriterConcurrency; i++)
{
    int workerId = i + 1;
    tasks.Add(RunWriterAsync(workerId, nodes, settings, replicationSamples.Writer, metrics, cancellation.Token));
}

for (int i = 0; i < settings.ReplicationProbeConcurrency; i++)
{
    tasks.Add(RunReplicationProbeAsync(nodes, settings, replicationSamples.Reader, metrics, cancellation.Token));
}

tasks.Add(RunReporterAsync(settings, metrics, cancellation.Token));

try
{
    await Task.WhenAll(tasks).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    // Graceful stop.
}
finally
{
    replicationSamples.Writer.TryComplete();

    foreach (SoakNode node in nodes)
    {
        node.Client.Dispose();
    }

    metrics.PrintFinal();
}

return;

static HttpClient CreateNodeClient(string baseUrl, SoakTestSettings settings)
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

static async Task RunWriterAsync(int workerId, IReadOnlyList<SoakNode> nodes, SoakTestSettings settings, ChannelWriter<WrittenDocument> samples, SoakMetrics metrics, CancellationToken cancellationToken)
{
    TimeSpan writeDelay = CalculateWriterDelay(settings);
    long workerSequence = 0L;

    while (!cancellationToken.IsCancellationRequested)
    {
        SoakNode node = nodes[Random.Shared.Next(0, nodes.Count)];
        LoadOrderDocument document = CreateDocument(workerId, Interlocked.Increment(ref workerSequence));
        Stopwatch stopwatch = Stopwatch.StartNew();

        bool success = await TryWriteAsync(node, settings.CollectionName, document, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (success)
        {
            metrics.RecordWriteSuccess(stopwatch.Elapsed);

            if (Random.Shared.NextDouble() <= settings.ReplicationSampleRate)
            {
                WrittenDocument sample = new WrittenDocument
                {
                    WriterNodeName = node.Name,
                    DocumentId = document.Id,
                    WrittenUtc = document.WrittenUtc
                };

                if (!samples.TryWrite(sample))
                {
                    metrics.RecordReplicationSampleDropped();
                }
            }
        }
        else
        {
            metrics.RecordWriteFailure(stopwatch.Elapsed);
        }

        if (writeDelay > TimeSpan.Zero)
        {
            await SafeDelayAsync(writeDelay, cancellationToken).ConfigureAwait(false);
        }
    }
}

static TimeSpan CalculateWriterDelay(SoakTestSettings settings)
{
    if (settings.TargetWritesPerSecond <= 0)
    {
        return TimeSpan.Zero;
    }

    double delayMilliseconds = (1000d * settings.WriterConcurrency) / settings.TargetWritesPerSecond;
    return TimeSpan.FromMilliseconds(Math.Max(1d, delayMilliseconds));
}

static async Task<bool> TryWriteAsync(SoakNode node, string collectionName, LoadOrderDocument document, CancellationToken cancellationToken)
{
    string endpoint = $"/api/documents/{Uri.EscapeDataString(collectionName)}/{Uri.EscapeDataString(document.Id)}";

    try
    {
        using HttpResponseMessage response = await node.Client.PutAsJsonAsync(endpoint, document, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return false;
    }
}

static async Task RunReplicationProbeAsync(IReadOnlyList<SoakNode> nodes, SoakTestSettings settings, ChannelReader<WrittenDocument> samples, SoakMetrics metrics, CancellationToken cancellationToken)
{
    await foreach (WrittenDocument sample in samples.ReadAllAsync(cancellationToken).ConfigureAwait(false))
    {
        List<SoakNode> readerNodes = nodes.Where(x => !string.Equals(x.Name, sample.WriterNodeName, StringComparison.Ordinal)).ToList();

        foreach (SoakNode node in readerNodes)
        {
            TimeSpan elapsed = await WaitForDocumentAsync(node, settings, sample.DocumentId, cancellationToken).ConfigureAwait(false);

            if (elapsed <= TimeSpan.FromSeconds(settings.ReplicationTimeoutSeconds))
            {
                metrics.RecordReplicationVisible(elapsed);
            }
            else
            {
                metrics.RecordReplicationTimeout(elapsed);
            }
        }
    }
}

static async Task<TimeSpan> WaitForDocumentAsync(SoakNode node, SoakTestSettings settings, string documentId, CancellationToken cancellationToken)
{
    TimeSpan timeout = TimeSpan.FromSeconds(settings.ReplicationTimeoutSeconds);
    TimeSpan pollDelay = TimeSpan.FromMilliseconds(settings.PollIntervalMilliseconds);
    Stopwatch stopwatch = Stopwatch.StartNew();

    while (!cancellationToken.IsCancellationRequested && stopwatch.Elapsed < timeout)
    {
        bool found = await TryReadDocumentAsync(node, settings.CollectionName, documentId, cancellationToken).ConfigureAwait(false);

        if (found)
        {
            return stopwatch.Elapsed;
        }

        await SafeDelayAsync(pollDelay, cancellationToken).ConfigureAwait(false);
    }

    return stopwatch.Elapsed;
}

static async Task<bool> TryReadDocumentAsync(SoakNode node, string collectionName, string documentId, CancellationToken cancellationToken)
{
    string endpoint = $"/api/documents/{Uri.EscapeDataString(collectionName)}/{Uri.EscapeDataString(documentId)}";

    try
    {
        using HttpResponseMessage response = await node.Client.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return false;
    }
}

static async Task RunReporterAsync(SoakTestSettings settings, SoakMetrics metrics, CancellationToken cancellationToken)
{
    TimeSpan reportInterval = TimeSpan.FromSeconds(settings.ReportIntervalSeconds);

    while (!cancellationToken.IsCancellationRequested)
    {
        await SafeDelayAsync(reportInterval, cancellationToken).ConfigureAwait(false);

        if (!cancellationToken.IsCancellationRequested)
        {
            metrics.PrintInterval();
        }
    }
}

static LoadOrderDocument CreateDocument(int workerId, long sequence)
{
    int quantity = Random.Shared.Next(1, 9);
    decimal unitPrice = decimal.Round((decimal)(Random.Shared.NextDouble() * 150 + 5), 2);
    DateTime now = DateTime.UtcNow;

    return new LoadOrderDocument
    {
        Id = $"load-{now:yyyyMMddHHmmssfff}-{workerId:D3}-{sequence:D8}-{Guid.NewGuid():N}",
        OrderId = $"order-{now:yyyyMMdd}-{Random.Shared.Next(1, 100_000):D6}",
        CustomerId = $"cust-{Random.Shared.Next(1, 50_000):D5}",
        StoreId = $"store-{Random.Shared.Next(1, 250):D3}",
        Region = PickRegion(),
        ItemSku = $"SKU-{Random.Shared.Next(1, 5000):D5}",
        Quantity = quantity,
        UnitPrice = unitPrice,
        TotalAmount = decimal.Round(quantity * unitPrice, 2),
        WrittenUtc = now,
        Notes = Convert.ToHexString(Guid.NewGuid().ToByteArray())
    };
}

static string PickRegion()
{
    string[] regions = { "north", "south", "east", "west", "central" };
    return regions[Random.Shared.Next(0, regions.Length)];
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
