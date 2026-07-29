using System.Collections.Concurrent;

namespace LiteDb.Distributed.Server.Infrastructure.Dashboard
{
    public class DashboardLatencyHistoryStore
    {
        internal const int RetentionMinutes = 60;
        internal const int MaxSamplesPerPeer = 720;
        internal const int MinimumSampleIntervalSeconds = 5;

        private readonly ConcurrentDictionary<string, PeerLatencyHistory> _historyByPeer = new(StringComparer.Ordinal);
        private static readonly TimeSpan RetentionWindow = TimeSpan.FromMinutes(RetentionMinutes);
        private static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromSeconds(MinimumSampleIntervalSeconds);

        public IReadOnlyList<DashboardLatencySampleDto> RecordAndGetHistory(string peerNodeId, DateTime timestampUtc, double? httpDurationMs, double? webSocketDurationMs)
        {
            string normalizedPeerNodeId = NormalizePeerNodeId(peerNodeId);
            if (string.IsNullOrWhiteSpace(normalizedPeerNodeId))
            {
                return Array.Empty<DashboardLatencySampleDto>();
            }

            DateTime normalizedTimestampUtc = NormalizeUtc(timestampUtc);
            PeerLatencyHistory history = _historyByPeer.GetOrAdd(normalizedPeerNodeId, _ => new PeerLatencyHistory());

            lock (history.Gate)
            {
                PruneLocked(history, normalizedTimestampUtc);

                DashboardLatencySampleDto? lastSample = history.Samples.Count == 0 ? null : history.Samples[^1];
                if (lastSample is null || normalizedTimestampUtc - lastSample.TimestampUtc >= MinimumSampleInterval)
                {
                    history.Samples.Add(new DashboardLatencySampleDto
                    {
                        TimestampUtc = normalizedTimestampUtc,
                        HttpDurationMs = NormalizeLatency(httpDurationMs),
                        WebSocketDurationMs = NormalizeLatency(webSocketDurationMs)
                    });
                }
                else
                {
                    history.Samples[^1] = new DashboardLatencySampleDto
                    {
                        TimestampUtc = normalizedTimestampUtc,
                        HttpDurationMs = NormalizeLatency(httpDurationMs),
                        WebSocketDurationMs = NormalizeLatency(webSocketDurationMs)
                    };
                }

                PruneLocked(history, normalizedTimestampUtc);
                return history.Samples.ToList();
            }
        }

        private void PruneLocked(PeerLatencyHistory history, DateTime utcNow)
        {
            DateTime oldestAllowedUtc = utcNow.Subtract(RetentionWindow);
            history.Samples.RemoveAll(x => x.TimestampUtc < oldestAllowedUtc);

            if (history.Samples.Count <= MaxSamplesPerPeer)
            {
                return;
            }

            int removeCount = history.Samples.Count - MaxSamplesPerPeer;
            history.Samples.RemoveRange(0, removeCount);
        }

        private static string NormalizePeerNodeId(string peerNodeId)
        {
            return (peerNodeId ?? string.Empty).Trim();
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            return value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
        }

        private static double? NormalizeLatency(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < 0)
            {
                return null;
            }

            return value.Value;
        }

        private class PeerLatencyHistory
        {
            public object Gate { get; } = new object();
            public List<DashboardLatencySampleDto> Samples { get; } = new List<DashboardLatencySampleDto>();
        }
    }
}
