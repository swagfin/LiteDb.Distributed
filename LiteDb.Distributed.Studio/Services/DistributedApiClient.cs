using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LiteDb.Distributed.Studio.Models;

namespace LiteDb.Distributed.Studio.Services;

public sealed class DistributedApiClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public Task<ApiResult<DashboardOverviewDto>> GetOverviewAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildAbsoluteUrl(baseUrl, "dashboard/api/overview"));
        return SendAsync<DashboardOverviewDto>(request, cancellationToken);
    }

    public async Task<ApiResult<List<string>>> GetCollectionsAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        var overviewResult = await GetOverviewAsync(profile.BaseUrl, cancellationToken).ConfigureAwait(false);

        if (!overviewResult.Success)
        {
            return ApiResult<List<string>>.Fail(
                overviewResult.ErrorMessage ?? "Failed loading server overview.",
                overviewResult.StatusCode,
                overviewResult.RawBody);
        }

        var database = overviewResult.Data?.Databases
            .FirstOrDefault(x => string.Equals(x.Name, profile.Database, StringComparison.OrdinalIgnoreCase));

        var collections = database?.BusinessCollections
            ?.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        return ApiResult<List<string>>.Ok(collections, overviewResult.StatusCode);
    }

    public Task<ApiResult<List<Dictionary<string, JsonElement>>>> ListDocumentsAsync(
        ConnectionProfile profile,
        string collection,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var safeSkip = Math.Max(skip, 0);
        var safeTake = Math.Clamp(take, 1, 10_000);
        var path = $"api/{Uri.EscapeDataString(collection)}?skip={safeSkip}&take={safeTake}";

        using var request = CreateDatabaseRequest(HttpMethod.Get, profile, path);
        return SendAsync<List<Dictionary<string, JsonElement>>>(request, cancellationToken);
    }

    public Task<ApiResult<Dictionary<string, JsonElement>>> GetDocumentByIdAsync(
        ConnectionProfile profile,
        string collection,
        string id,
        CancellationToken cancellationToken = default)
    {
        var path = $"api/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(id)}";

        using var request = CreateDatabaseRequest(HttpMethod.Get, profile, path);
        return SendAsync<Dictionary<string, JsonElement>>(request, cancellationToken);
    }

    public Task<ApiResult<WriteResultDto>> PutDocumentAsync(
        ConnectionProfile profile,
        string collection,
        string id,
        string payloadJson,
        string? parentVersion = null,
        CancellationToken cancellationToken = default)
    {
        var path = BuildVersionedPath(
            $"api/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(id)}",
            parentVersion);

        using var request = CreateDatabaseRequest(HttpMethod.Put, profile, path);
        request.Content = CreateJsonContent(payloadJson);

        return SendAsync<WriteResultDto>(request, cancellationToken);
    }

    public Task<ApiResult<WriteResultDto>> DeleteDocumentAsync(
        ConnectionProfile profile,
        string collection,
        string id,
        string? parentVersion = null,
        CancellationToken cancellationToken = default)
    {
        var path = BuildVersionedPath(
            $"api/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(id)}",
            parentVersion);

        using var request = CreateDatabaseRequest(HttpMethod.Delete, profile, path);
        return SendAsync<WriteResultDto>(request, cancellationToken);
    }

    public Task<ApiResult<QueryResponseDto>> ExecuteQueryAsync(
        ConnectionProfile profile,
        string query,
        int take = 200,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            Query = query,
            Take = Math.Clamp(take, 1, 10_000)
        };

        using var request = CreateDatabaseRequest(HttpMethod.Post, profile, "api/query");
        request.Content = CreateJsonContent(JsonSerializer.Serialize(payload, JsonOptions));

        return SendAsync<QueryResponseDto>(request, cancellationToken);
    }

    private HttpRequestMessage CreateDatabaseRequest(HttpMethod method, ConnectionProfile profile, string relativePath)
    {
        var request = new HttpRequestMessage(method, BuildAbsoluteUrl(profile.BaseUrl, relativePath));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Database", profile.Database);
        request.Headers.Add(profile.CredentialHeaderName, profile.Credential);

        return request;
    }

    private async Task<ApiResult<T>> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return ApiResult<T>.Fail(BuildErrorMessage(response.StatusCode, body), response.StatusCode, body);
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return ApiResult<T>.Ok(default, response.StatusCode);
            }

            try
            {
                var data = JsonSerializer.Deserialize<T>(body, JsonOptions);
                return ApiResult<T>.Ok(data, response.StatusCode);
            }
            catch (JsonException ex)
            {
                return ApiResult<T>.Fail($"Invalid server JSON payload: {ex.Message}", response.StatusCode, body);
            }
        }
        catch (OperationCanceledException)
        {
            return ApiResult<T>.Fail("Request canceled or timed out.");
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<T>.Fail($"Network request failed: {ex.Message}");
        }
    }

    private static StringContent CreateJsonContent(string payloadJson)
    {
        return new StringContent(payloadJson, Encoding.UTF8, "application/json");
    }

    private static string BuildVersionedPath(string path, string? parentVersion)
    {
        if (string.IsNullOrWhiteSpace(parentVersion))
        {
            return path;
        }

        return $"{path}?parentVersion={Uri.EscapeDataString(parentVersion)}";
    }

    private static string BuildAbsoluteUrl(string baseUrl, string relativePath)
    {
        var normalized = baseUrl.TrimEnd('/');
        return $"{normalized}/{relativePath.TrimStart('/')}";
    }

    private static string BuildErrorMessage(HttpStatusCode statusCode, string? body)
    {
        var prefix = $"HTTP {(int)statusCode}";

        if (string.IsNullOrWhiteSpace(body))
        {
            return prefix;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("Error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                return $"{prefix}: {error.GetString()}";
            }
        }
        catch
        {
            // Ignore JSON parsing failure for error payloads.
        }

        return $"{prefix}: {body}";
    }
}
