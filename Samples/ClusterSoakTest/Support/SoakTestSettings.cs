using System.Text.Json;

namespace ClusterSoakTest.Support
{
    public class SoakTestSettings
    {
        public string[] Nodes { get; set; } =
        {
            "http://localhost:17001",
            "http://localhost:17002",
            "http://localhost:17003"
        };

        public string Database { get; set; } = "testapp";
        public string ApiKey { get; set; } = "root";
        public string CollectionName { get; set; } = "LoadOrders";
        public int DurationSeconds { get; set; } = 300;
        public int WriterConcurrency { get; set; } = 16;
        public int TargetWritesPerSecond { get; set; } = 500;
        public double ReplicationSampleRate { get; set; } = 0.02;
        public int ReplicationProbeConcurrency { get; set; } = 8;
        public int ReplicationQueueCapacity { get; set; } = 10_000;
        public int ReplicationTimeoutSeconds { get; set; } = 30;
        public int PollIntervalMilliseconds { get; set; } = 100;
        public int ReportIntervalSeconds { get; set; } = 5;
        public int HttpTimeoutSeconds { get; set; } = 30;

        public static SoakTestSettings Load()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "sample-settings.json");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Missing configuration file '{path}'.");
            }

            SoakTestSettings settings = JsonSerializer.Deserialize<SoakTestSettings>(File.ReadAllText(path)) ?? new SoakTestSettings();
            string[] nodes = settings.Nodes.Where(x => !string.IsNullOrWhiteSpace(x)).Select(NormalizeSettingsBaseUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            if (nodes.Length < 2)
            {
                throw new InvalidOperationException("sample-settings.json must define at least 2 unique node URLs.");
            }

            if (string.IsNullOrWhiteSpace(settings.Database) || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                throw new InvalidOperationException("sample-settings.json is missing required Database/ApiKey.");
            }

            return new SoakTestSettings
            {
                Nodes = nodes,
                Database = settings.Database.Trim(),
                ApiKey = settings.ApiKey.Trim(),
                CollectionName = string.IsNullOrWhiteSpace(settings.CollectionName) ? "LoadOrders" : settings.CollectionName.Trim(),
                DurationSeconds = Math.Max(0, settings.DurationSeconds),
                WriterConcurrency = Math.Clamp(settings.WriterConcurrency, 1, 1024),
                TargetWritesPerSecond = Math.Max(0, settings.TargetWritesPerSecond),
                ReplicationSampleRate = Math.Clamp(settings.ReplicationSampleRate, 0d, 1d),
                ReplicationProbeConcurrency = Math.Clamp(settings.ReplicationProbeConcurrency, 1, 512),
                ReplicationQueueCapacity = Math.Clamp(settings.ReplicationQueueCapacity, 100, 1_000_000),
                ReplicationTimeoutSeconds = Math.Clamp(settings.ReplicationTimeoutSeconds, 1, 3600),
                PollIntervalMilliseconds = Math.Clamp(settings.PollIntervalMilliseconds, 10, 60_000),
                ReportIntervalSeconds = Math.Clamp(settings.ReportIntervalSeconds, 1, 3600),
                HttpTimeoutSeconds = Math.Clamp(settings.HttpTimeoutSeconds, 1, 3600)
            };
        }

        private static string NormalizeSettingsBaseUrl(string baseUrl)
        {
            return baseUrl.Trim().TrimEnd('/');
        }
    }
}
