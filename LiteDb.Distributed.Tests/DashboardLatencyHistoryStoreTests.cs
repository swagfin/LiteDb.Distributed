using LiteDb.Distributed.Server.Infrastructure.Dashboard;

namespace LiteDb.Distributed.Tests
{
    public class DashboardLatencyHistoryStoreTests
    {
        [Fact]
        public void RecordAndGetHistory_ReplacesSampleInsideMinimumInterval()
        {
            DashboardLatencyHistoryStore store = new DashboardLatencyHistoryStore();
            DateTime startedUtc = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

            IReadOnlyList<DashboardLatencySampleDto> first = store.RecordAndGetHistory("node-2", startedUtc, 10, 20);
            IReadOnlyList<DashboardLatencySampleDto> second = store.RecordAndGetHistory("node-2", startedUtc.AddSeconds(2), 30, 40);
            IReadOnlyList<DashboardLatencySampleDto> third = store.RecordAndGetHistory("node-2", startedUtc.AddSeconds(7), 50, 60);

            Assert.Single(first);
            Assert.Single(second);
            Assert.Equal(startedUtc.AddSeconds(2), second[0].TimestampUtc);
            Assert.Equal(30, second[0].HttpDurationMs);
            Assert.Equal(40, second[0].WebSocketDurationMs);
            Assert.Equal(2, third.Count);
            Assert.Equal(startedUtc.AddSeconds(7), third[1].TimestampUtc);
        }

        [Fact]
        public void RecordAndGetHistory_PrunesByRetentionAndMaxSamples()
        {
            DashboardLatencyHistoryStore store = new DashboardLatencyHistoryStore();
            DateTime startedUtc = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

            store.RecordAndGetHistory("node-2", startedUtc, 10, 20);
            store.RecordAndGetHistory("node-2", startedUtc.AddMinutes(30), 20, 30);
            IReadOnlyList<DashboardLatencySampleDto> history = store.RecordAndGetHistory("node-2", startedUtc.AddMinutes(61), 30, 40);

            Assert.Equal(2, history.Count);
            Assert.All(history, x => Assert.True(x.TimestampUtc >= startedUtc.AddMinutes(1)));
            Assert.Equal(startedUtc.AddMinutes(61), history[^1].TimestampUtc);
        }

        [Fact]
        public void RecordAndGetHistory_PrunesToHardcodedMaxSamples()
        {
            DashboardLatencyHistoryStore store = new DashboardLatencyHistoryStore();
            DateTime startedUtc = new DateTime(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
            IReadOnlyList<DashboardLatencySampleDto> history = Array.Empty<DashboardLatencySampleDto>();

            for (int index = 0; index < 725; index++)
            {
                history = store.RecordAndGetHistory("node-2", startedUtc.AddSeconds(index * 5), index, index);
            }

            Assert.Equal(720, history.Count);
            Assert.Equal(startedUtc.AddSeconds(25), history[0].TimestampUtc);
            Assert.Equal(startedUtc.AddSeconds(724 * 5), history[^1].TimestampUtc);
        }

        [Fact]
        public void RecordAndGetHistory_NormalizesInvalidLatencyValues()
        {
            DashboardLatencyHistoryStore store = new DashboardLatencyHistoryStore();

            IReadOnlyList<DashboardLatencySampleDto> history = store.RecordAndGetHistory("node-2", DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified), double.NaN, -1);

            Assert.Single(history);
            Assert.Null(history[0].HttpDurationMs);
            Assert.Null(history[0].WebSocketDurationMs);
            Assert.Equal(DateTimeKind.Utc, history[0].TimestampUtc.Kind);
        }
    }
}
