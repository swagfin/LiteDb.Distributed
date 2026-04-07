using LiteDb.Distributed.Studio.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LiteDb.Distributed.Studio.Services
{
    public class DistributedApiClient
    {
        private readonly HttpClient _httpClient;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        public DistributedApiClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public Task<ApiResult<DashboardOverviewDto>> GetOverviewAsync(string baseUrl, CancellationToken cancellationToken = default)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, BuildAbsoluteUrl(baseUrl, "dashboard/api/overview"));
            return SendAsync<DashboardOverviewDto>(request, cancellationToken);
        }

        public async Task<ApiResult<List<string>>> GetCollectionsAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
        {
            ApiResult<DashboardOverviewDto> overviewResult = await GetOverviewAsync(profile.BaseUrl, cancellationToken).ConfigureAwait(false);

            if (!overviewResult.Success)
            {
                return ApiResult<List<string>>.Fail(overviewResult.ErrorMessage ?? "Failed loading server overview.", overviewResult.StatusCode, overviewResult.RawBody);
            }

            DashboardDatabaseStatusDto? database = overviewResult.Data?.Databases.FirstOrDefault(x => string.Equals(x.Name, profile.Database, StringComparison.OrdinalIgnoreCase));

            List<string> collections = database?.BusinessCollections?.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() ?? [];

            return ApiResult<List<string>>.Ok(collections, overviewResult.StatusCode);
        }

        public Task<ApiResult<List<Dictionary<string, JsonElement>>>> ListDocumentsAsync(ConnectionProfile profile, string collection, int skip, int take, CancellationToken cancellationToken = default)
        {
            int safeSkip = Math.Max(skip, 0);
            int safeTake = Math.Clamp(take, 1, 10_000);
            string path = $"api/{Uri.EscapeDataString(collection)}?skip={safeSkip}&take={safeTake}";

            using HttpRequestMessage request = CreateDatabaseRequest(HttpMethod.Get, profile, path);
            return SendAsync<List<Dictionary<string, JsonElement>>>(request, cancellationToken);
        }

        public Task<ApiResult<Dictionary<string, JsonElement>>> GetDocumentByIdAsync(ConnectionProfile profile, string collection, string id, CancellationToken cancellationToken = default)
        {
            string path = $"api/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(id)}";

            using HttpRequestMessage request = CreateDatabaseRequest(HttpMethod.Get, profile, path);
            return SendAsync<Dictionary<string, JsonElement>>(request, cancellationToken);
        }

        public Task<ApiResult<Dictionary<string, JsonElement>>> GetCacheEntryAsync(ConnectionProfile profile, string key, CancellationToken cancellationToken = default)
        {
            string path = $"api/cache/{Uri.EscapeDataString(key)}";

            using HttpRequestMessage request = CreateDatabaseRequest(HttpMethod.Get, profile, path);
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
            string path = BuildVersionedPath($"api/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(id)}", parentVersion);

            using HttpRequestMessage request = CreateDatabaseRequest(HttpMethod.Put, profile, path);
            request.Content = CreateJsonContent(payloadJson);

            return SendAsync<WriteResultDto>(request, cancellationToken);
        }

        public Task<ApiResult<WriteResultDto>> DeleteDocumentAsync(ConnectionProfile profile, string collection, string id, string? parentVersion = null, CancellationToken cancellationToken = default)
        {
            string path = BuildVersionedPath($"api/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(id)}", parentVersion);

            using HttpRequestMessage request = CreateDatabaseRequest(HttpMethod.Delete, profile, path);
            return SendAsync<WriteResultDto>(request, cancellationToken);
        }

        public Task<ApiResult<JsonElement>> RegisterCollectionAsync(ConnectionProfile profile, string collection, CancellationToken cancellationToken = default)
        {
            string path = $"api/{Uri.EscapeDataString(collection)}/register";
            using HttpRequestMessage request = CreateDatabaseRequest(HttpMethod.Post, profile, path);
            return SendAsync<JsonElement>(request, cancellationToken);
        }

        public Task<ApiResult<QueryResponseDto>> ExecuteQueryAsync(ConnectionProfile profile, string query, int take = 200, CancellationToken cancellationToken = default)
        {
            QueryRequestPayload payload = new QueryRequestPayload
            {
                Query = query,
                Take = Math.Clamp(take, 1, 10_000)
            };

            using HttpRequestMessage request = CreateDatabaseRequest(HttpMethod.Post, profile, "api/query");
            request.Content = CreateJsonContent(JsonSerializer.Serialize(payload, JsonOptions));

            return SendAsync<QueryResponseDto>(request, cancellationToken);
        }

        private HttpRequestMessage CreateDatabaseRequest(HttpMethod method, ConnectionProfile profile, string relativePath)
        {
            HttpRequestMessage request = new HttpRequestMessage(method, BuildAbsoluteUrl(profile.BaseUrl, relativePath));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("Database", profile.Database);
            request.Headers.Add("ApiKey", profile.Credential);

            return request;
        }

        private async Task<ApiResult<T>> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

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
                    T? data = JsonSerializer.Deserialize<T>(body, JsonOptions);
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
            string normalized = baseUrl.TrimEnd('/');
            return $"{normalized}/{relativePath.TrimStart('/')}";
        }

        private static string BuildErrorMessage(HttpStatusCode statusCode, string? body)
        {
            string prefix = $"HTTP {(int)statusCode}";

            if (string.IsNullOrWhiteSpace(body))
            {
                return prefix;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("Error", out JsonElement error)
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

        private class QueryRequestPayload
        {
            public string Query { get; set; } = string.Empty;
            public int Take { get; set; }
        }
    }

}
