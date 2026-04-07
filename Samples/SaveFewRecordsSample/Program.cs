using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

SampleSettings settings = SampleSettings.Load();

using IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton(settings);
        services.AddHostedService<OrderTransactionGeneratorService>();
    })
    .Build();

await host.RunAsync().ConfigureAwait(false);

public class OrderTransactionGeneratorService : BackgroundService
{
    private readonly SampleSettings _settings;
    private readonly ILogger<OrderTransactionGeneratorService> _logger;
    private readonly HttpClient _httpClient;

    public OrderTransactionGeneratorService(SampleSettings settings, ILogger<OrderTransactionGeneratorService> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_settings.ServerUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };

        _httpClient.DefaultRequestHeaders.Add("Database", _settings.Database);
        _httpClient.DefaultRequestHeaders.Add("ApiKey", _settings.ApiKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OrderTransaction generator started.");
        _logger.LogInformation("Server={Server} Database={Database} Collection={Collection}", _settings.ServerUrl, _settings.Database, _settings.CollectionName);

        long sequence = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            int waitSeconds = Random.Shared.Next(_settings.MinIntervalSeconds, _settings.MaxIntervalSeconds + 1);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            OrderTransaction transaction = CreateTransaction(Interlocked.Increment(ref sequence));
            await UpsertAsync(transaction, stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation("OrderTransaction generator stopped.");
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }

    private OrderTransaction CreateTransaction(long sequence)
    {
        int quantity = Random.Shared.Next(1, 8);
        decimal unitPrice = decimal.Round((decimal)(Random.Shared.NextDouble() * 95 + 5), 2);
        string transactionId = $"ordtx-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{sequence:D6}";

        return new OrderTransaction
        {
            Id = transactionId,
            OrderId = $"order-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1, 5000):D4}",
            CustomerId = $"cust-{Random.Shared.Next(1, 1000):D4}",
            ItemSku = $"SKU-{Random.Shared.Next(1, 200):D4}",
            Quantity = quantity,
            UnitPrice = unitPrice,
            TotalAmount = decimal.Round(quantity * unitPrice, 2),
            OccurredUtc = DateTime.UtcNow,
            Source = "SaveFewRecordsSample"
        };
    }

    private async Task UpsertAsync(OrderTransaction transaction, CancellationToken cancellationToken)
    {
        string endpoint = $"/api/{_settings.CollectionName}/{transaction.Id}";
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using HttpResponseMessage response = await _httpClient.PutAsJsonAsync(endpoint, transaction, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Insert failed. Status={Status} Endpoint={Endpoint} TxId={TxId} SaveDurationMs={SaveDurationMs} Body={Body}",
                    (int)response.StatusCode,
                    endpoint,
                    transaction.Id,
                    stopwatch.Elapsed.TotalMilliseconds,
                    body);
                return;
            }

            _logger.LogInformation(
                "Inserted OrderTransaction Id={Id} Customer={CustomerId} Item={ItemSku} Qty={Qty} Total={Total} SaveDurationMs={SaveDurationMs}",
                transaction.Id,
                transaction.CustomerId,
                transaction.ItemSku,
                transaction.Quantity,
                transaction.TotalAmount,
                stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogWarning("Insert timed out. TxId={TxId} SaveDurationMs={SaveDurationMs}", transaction.Id, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Insert failed unexpectedly. TxId={TxId} SaveDurationMs={SaveDurationMs}", transaction.Id, stopwatch.Elapsed.TotalMilliseconds);
        }
    }
}

public sealed record SampleSettings
{
    public string ServerUrl { get; init; } = "http://localhost:1446";
    public string Database { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string CollectionName { get; init; } = "OrderTransactions";
    public int MinIntervalSeconds { get; init; } = 1;
    public int MaxIntervalSeconds { get; init; } = 3;

    public static SampleSettings Load()
    {
        string settingsPath = Path.Combine(AppContext.BaseDirectory, "sample-settings.json");
        if (!File.Exists(settingsPath))
        {
            throw new FileNotFoundException($"Missing configuration file '{settingsPath}'.");
        }

        SampleSettings settings = JsonSerializer.Deserialize<SampleSettings>(File.ReadAllText(settingsPath)) ?? new SampleSettings();

        if (string.IsNullOrWhiteSpace(settings.ServerUrl)
            || string.IsNullOrWhiteSpace(settings.Database)
            || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("sample-settings.json is missing required values: ServerUrl, Database, ApiKey.");
        }

        int minInterval = Math.Max(1, settings.MinIntervalSeconds);
        int maxInterval = Math.Max(minInterval, settings.MaxIntervalSeconds);

        return settings with
        {
            ServerUrl = settings.ServerUrl.Trim(),
            Database = settings.Database.Trim(),
            ApiKey = settings.ApiKey.Trim(),
            CollectionName = string.IsNullOrWhiteSpace(settings.CollectionName) ? "OrderTransactions" : settings.CollectionName.Trim(),
            MinIntervalSeconds = minInterval,
            MaxIntervalSeconds = maxInterval
        };
    }
}

public sealed record OrderTransaction
{
    public required string Id { get; init; }
    public required string OrderId { get; init; }
    public required string CustomerId { get; init; }
    public required string ItemSku { get; init; }
    public required int Quantity { get; init; }
    public required decimal UnitPrice { get; init; }
    public required decimal TotalAmount { get; init; }
    public required DateTime OccurredUtc { get; init; }
    public required string Source { get; init; }
}



