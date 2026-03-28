using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

var settings = LoadSettings();

using var httpClient = new HttpClient
{
    BaseAddress = new Uri(settings.ServerUrl),
    Timeout = TimeSpan.FromSeconds(20)
};

httpClient.DefaultRequestHeaders.Add("Database", settings.Database);
httpClient.DefaultRequestHeaders.Add("ApiKey", settings.ApiKey);
httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

Console.WriteLine("LiteDb.Distributed Sample Client");
Console.WriteLine($"Server: {settings.ServerUrl}");
Console.WriteLine($"Database: {settings.Database.ToLowerInvariant()}");
Console.WriteLine("The app keeps running. Press ENTER to run again or type 'q' to quit.");

while (true)
{
    try
    {
        await RunSampleOnceAsync(httpClient).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Unexpected error: {ex.Message}");
    }

    Console.Write("> ");
    var input = Console.ReadLine();

    if (string.Equals(input, "q", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }
}

static SampleSettings LoadSettings()
{
    var settingsPath = Path.Combine(AppContext.BaseDirectory, "sample-settings.json");
    if (!File.Exists(settingsPath))
    {
        throw new FileNotFoundException($"Missing configuration file '{settingsPath}'. Create sample-settings.json and try again.");
    }

    SampleSettings settings = JsonSerializer.Deserialize<SampleSettings>(File.ReadAllText(settingsPath)) ?? new SampleSettings();

    if (string.IsNullOrWhiteSpace(settings.ServerUrl)
        || string.IsNullOrWhiteSpace(settings.Database)
        || string.IsNullOrWhiteSpace(settings.ApiKey))
    {
        throw new InvalidOperationException("sample-settings.json is missing required values.");
    }

    return settings with
    {
        ServerUrl = settings.ServerUrl.Trim(),
        Database = settings.Database.Trim(),
        ApiKey = settings.ApiKey.Trim()
    };
}

static async Task RunSampleOnceAsync(HttpClient httpClient)
{
    var now = DateTime.UtcNow;
    var records = new List<(string Collection, string Id, object Body)>
    {
        (
            "Customers",
            "sample-cust-001",
            new
            {
                Id = "sample-cust-001",
                Name = "Sample Customer 001",
                Email = "customer001@example.com",
                UpdatedUtc = now
            }),
        (
            "Customers",
            "sample-cust-002",
            new
            {
                Id = "sample-cust-002",
                Name = "Sample Customer 002",
                Email = "customer002@example.com",
                UpdatedUtc = now
            }),
        (
            "Items",
            "sample-item-001",
            new
            {
                Id = "sample-item-001",
                Sku = "SAMPLE-SKU-001",
                Name = "Sample Item 001",
                UnitPrice = 12.5m,
                UpdatedUtc = now
            })
    };

    foreach (var record in records)
    {
        await SaveRecordAsync(httpClient, record.Collection, record.Id, record.Body).ConfigureAwait(false);
    }

    var customers = await SafeGetAsync("/api/Customers?skip=0&take=50", httpClient).ConfigureAwait(false);
    var items = await SafeGetAsync("/api/Items?skip=0&take=50", httpClient).ConfigureAwait(false);

    Console.WriteLine($"Customers visible on node: {customers?.Count ?? 0}");
    Console.WriteLine($"Items visible on node: {items?.Count ?? 0}");
}

static async Task SaveRecordAsync(HttpClient httpClient, string collection, string id, object body)
{
    var endpoint = $"/api/{collection}/{id}";

    try
    {
        using var response = await httpClient.PutAsJsonAsync(endpoint, body).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Console.WriteLine($"Save failed for {collection}/{id}. Status={(int)response.StatusCode} Body={error}");
            return;
        }

        Console.WriteLine($"Saved {collection}/{id}");
    }
    catch (TaskCanceledException)
    {
        Console.WriteLine($"Save timed out for {collection}/{id}");
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"Network error while saving {collection}/{id}: {ex.Message}");
    }
}

static async Task<List<Dictionary<string, object>>?> SafeGetAsync(string endpoint, HttpClient httpClient)
{
    try
    {
        using var response = await httpClient.GetAsync(endpoint).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Console.WriteLine($"Query failed for {endpoint}. Status={(int)response.StatusCode} Body={error}");
            return null;
        }

        return await response
            .Content
            .ReadFromJsonAsync<List<Dictionary<string, object>>>()
            .ConfigureAwait(false);
    }
    catch (TaskCanceledException)
    {
        Console.WriteLine($"Query timed out for {endpoint}");
        return null;
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"Network error while querying {endpoint}: {ex.Message}");
        return null;
    }
}

public sealed record SampleSettings
{
    public string ServerUrl { get; init; } = "http://localhost:1446";
    public string Database { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
}
