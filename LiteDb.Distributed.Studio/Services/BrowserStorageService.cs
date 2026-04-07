using System.Text.Json;
using Microsoft.JSInterop;

namespace LiteDb.Distributed.Studio.Services;

public sealed class BrowserStorageService(IJSRuntime jsRuntime)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetAsync<T>(string key)
    {
        var raw = await jsRuntime.InvokeAsync<string?>("liteDbStudioStorage.get", key).ConfigureAwait(false);

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
        var raw = JsonSerializer.Serialize(value, JsonOptions);
        return jsRuntime.InvokeVoidAsync("liteDbStudioStorage.set", key, raw).AsTask();
    }

    public Task RemoveAsync(string key)
    {
        return jsRuntime.InvokeVoidAsync("liteDbStudioStorage.remove", key).AsTask();
    }
}
