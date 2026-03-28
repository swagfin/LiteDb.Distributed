using System.Net.Http.Headers;
using System.Net.Http.Json;

var serverUrl = Environment.GetEnvironmentVariable("DLITEDB_SERVER_URL") ?? "http://localhost:1446";
var databaseName = (Environment.GetEnvironmentVariable("DLITEDB_DATABASE") ?? "testapp").Trim();
var apiKey = (Environment.GetEnvironmentVariable("DLITEDB_API_KEY") ?? "sample-local-key").Trim();

using var httpClient = new HttpClient { BaseAddress = new Uri(serverUrl) };
httpClient.DefaultRequestHeaders.Add("Database", databaseName);
httpClient.DefaultRequestHeaders.Add("ApiKey", apiKey);
httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

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

Console.WriteLine($"Server: {serverUrl}");
Console.WriteLine($"Database: {databaseName.ToLowerInvariant()}");

foreach (var record in records)
{
    var endpoint = $"/api/{record.Collection}/{record.Id}";
    using var response = await httpClient.PutAsJsonAsync(endpoint, record.Body).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    Console.WriteLine($"Saved {record.Collection}/{record.Id}");
}

var customers = await httpClient
    .GetFromJsonAsync<List<Dictionary<string, object>>>("/api/Customers?skip=0&take=50")
    .ConfigureAwait(false);

var items = await httpClient
    .GetFromJsonAsync<List<Dictionary<string, object>>>("/api/Items?skip=0&take=50")
    .ConfigureAwait(false);

Console.WriteLine($"Customers visible on node: {customers?.Count ?? 0}");
Console.WriteLine($"Items visible on node: {items?.Count ?? 0}");
