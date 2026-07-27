using System.Text.Json;

namespace DistributedCacheProbe.Support
{
    public class CacheProbeSettings
    {
        public string[] Nodes { get; set; } =
        {
            "http://localhost:17001",
            "http://localhost:17002",
            "http://localhost:17003"
        };

        public string Database { get; set; } = "testapp";
        public string ApiKey { get; set; } = "root";
        public int PollIntervalMilliseconds { get; set; } = 25;
        public int VisibilityTimeoutSeconds { get; set; } = 20;
        public int MinPauseMilliseconds { get; set; } = 500;
        public int MaxPauseMilliseconds { get; set; } = 1500;
        public int MinRandomTtlMinutes { get; set; } = 1;
        public int MaxRandomTtlMinutes { get; set; } = 3;
        public int HttpTimeoutSeconds { get; set; } = 10;

        public static CacheProbeSettings Load()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "sample-settings.json");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Missing configuration file '{path}'.");
            }

            CacheProbeSettings settings = JsonSerializer.Deserialize<CacheProbeSettings>(File.ReadAllText(path)) ?? new CacheProbeSettings();
            string[] normalizedNodes = settings.Nodes.Where(x => !string.IsNullOrWhiteSpace(x)).Select(NormalizeBaseUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            if (normalizedNodes.Length < 2)
            {
                throw new InvalidOperationException("sample-settings.json must define at least 2 unique node URLs.");
            }

            if (string.IsNullOrWhiteSpace(settings.Database) || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                throw new InvalidOperationException("sample-settings.json is missing required Database/ApiKey.");
            }

            int minPauseMs = Math.Max(100, settings.MinPauseMilliseconds);
            int minTtlMinutes = Math.Max(1, settings.MinRandomTtlMinutes);
            int maxTtlMinutes = Math.Max(minTtlMinutes, settings.MaxRandomTtlMinutes);

            return new CacheProbeSettings
            {
                Nodes = normalizedNodes,
                Database = settings.Database.Trim(),
                ApiKey = settings.ApiKey.Trim(),
                PollIntervalMilliseconds = Math.Clamp(settings.PollIntervalMilliseconds, 10, 60_000),
                VisibilityTimeoutSeconds = Math.Clamp(settings.VisibilityTimeoutSeconds, 2, 3600),
                MinPauseMilliseconds = minPauseMs,
                MaxPauseMilliseconds = Math.Max(minPauseMs, settings.MaxPauseMilliseconds),
                MinRandomTtlMinutes = minTtlMinutes,
                MaxRandomTtlMinutes = maxTtlMinutes,
                HttpTimeoutSeconds = Math.Clamp(settings.HttpTimeoutSeconds, 1, 3600)
            };
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            return baseUrl.Trim().TrimEnd('/');
        }
    }
}
