using Blazored.LocalStorage;

namespace LiteDb.Distributed.Studio.Services
{
    public class BrowserStorageService
    {
        private const string KeyPrefix = "litedb.distributed.studio";
        private readonly ILocalStorageService _localStorage;

        public BrowserStorageService(ILocalStorageService localStorage)
        {
            _localStorage = localStorage;
        }

        public async Task<T?> GetAsync<T>(string key)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return default;
                }

                string scopedKey = BuildScopedKey(key);
                bool exists = await _localStorage.ContainKeyAsync(scopedKey).ConfigureAwait(false);
                if (!exists)
                {
                    return default;
                }

                StorageRecord<T>? record = await _localStorage.GetItemAsync<StorageRecord<T>>(scopedKey).ConfigureAwait(false);
                return record is null ? default : record.Value;
            }
            catch
            {
                return default;
            }
        }

        public async Task SetAsync<T>(string key, T value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Storage key is required.", nameof(key));
            }

            StorageRecord<T> record = new StorageRecord<T>
            {
                Value = value,
                SavedAtUtc = DateTime.UtcNow
            };

            await _localStorage.SetItemAsync(BuildScopedKey(key), record).ConfigureAwait(false);
        }

        public async Task RemoveAsync(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            await _localStorage.RemoveItemAsync(BuildScopedKey(key)).ConfigureAwait(false);
        }

        private static string BuildScopedKey(string key)
        {
            return $"{KeyPrefix}:{key}";
        }

        private class StorageRecord<T>
        {
            public T? Value { get; set; }
            public DateTime SavedAtUtc { get; set; }
        }
    }

}
