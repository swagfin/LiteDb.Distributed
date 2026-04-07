using System.Text.Json;
using Microsoft.JSInterop;

namespace LiteDb.Distributed.Studio.Services
{
    public class BrowserStorageService
    {
        private readonly IJSRuntime _jsRuntime;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public BrowserStorageService(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        public async Task<T?> GetAsync<T>(string key)
        {
            string? raw = await _jsRuntime.InvokeAsync<string?>("liteDbStudioStorage.get", key).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(raw))
            {
                return default;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(raw, JsonOptions);
            }
            catch
            {
                return default;
            }
        }

        public Task SetAsync<T>(string key, T value)
        {
            string raw = JsonSerializer.Serialize(value, JsonOptions);
            return _jsRuntime.InvokeVoidAsync("liteDbStudioStorage.set", key, raw).AsTask();
        }

        public Task RemoveAsync(string key)
        {
            return _jsRuntime.InvokeVoidAsync("liteDbStudioStorage.remove", key).AsTask();
        }
    }

}

