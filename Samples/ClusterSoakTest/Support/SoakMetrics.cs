using System.Collections.Concurrent;
using System.Diagnostics;

namespace ClusterSoakTest.Support
{
    public class SoakMetrics
    {
        private readonly Stopwatch _totalStopwatch = Stopwatch.StartNew();
        private readonly Stopwatch _intervalStopwatch = Stopwatch.StartNew();
        private readonly ConcurrentQueue<double> _writeLatencies = new ConcurrentQueue<double>();
        private readonly ConcurrentQueue<double> _replicationLatencies = new ConcurrentQueue<double>();
        private long _writeSuccessTotal;
        private long _writeFailureTotal;
        private long _replicationVisibleTotal;
        private long _replicationTimeoutTotal;
        private long _replicationSampleDroppedTotal;
        private long _lastWriteSuccess;
        private long _lastWriteFailure;

        public void RecordWriteSuccess(TimeSpan elapsed)
        {
            Interlocked.Increment(ref _writeSuccessTotal);
            _writeLatencies.Enqueue(elapsed.TotalMilliseconds);
        }

        public void RecordWriteFailure(TimeSpan elapsed)
        {
            Interlocked.Increment(ref _writeFailureTotal);
            _writeLatencies.Enqueue(elapsed.TotalMilliseconds);
        }

        public void RecordReplicationVisible(TimeSpan elapsed)
        {
            Interlocked.Increment(ref _replicationVisibleTotal);
            _replicationLatencies.Enqueue(elapsed.TotalMilliseconds);
        }

        public void RecordReplicationTimeout(TimeSpan elapsed)
        {
            Interlocked.Increment(ref _replicationTimeoutTotal);
            _replicationLatencies.Enqueue(elapsed.TotalMilliseconds);
        }

        public void RecordReplicationSampleDropped()
        {
            Interlocked.Increment(ref _replicationSampleDroppedTotal);
        }

        public void PrintInterval()
        {
            long writeSuccess = Interlocked.Read(ref _writeSuccessTotal);
            long writeFailure = Interlocked.Read(ref _writeFailureTotal);
            long intervalWriteSuccess = writeSuccess - Interlocked.Exchange(ref _lastWriteSuccess, writeSuccess);
            long intervalWriteFailure = writeFailure - Interlocked.Exchange(ref _lastWriteFailure, writeFailure);
            double intervalSeconds = Math.Max(0.001d, _intervalStopwatch.Elapsed.TotalSeconds);
            _intervalStopwatch.Restart();

            LatencySnapshot writeLatency = DrainLatencySnapshot(_writeLatencies);
            LatencySnapshot replicationLatency = DrainLatencySnapshot(_replicationLatencies);

            Console.WriteLine(
                "[{0:hh\\:mm\\:ss}] writes ok={1} fail={2} rps={3:F1} write p50/p95/p99={4} replication visible={5} timeout={6} droppedSamples={7} repl p50/p95/p99={8}",
                _totalStopwatch.Elapsed,
                writeSuccess,
                writeFailure,
                intervalWriteSuccess / intervalSeconds,
                writeLatency,
                Interlocked.Read(ref _replicationVisibleTotal),
                Interlocked.Read(ref _replicationTimeoutTotal),
                Interlocked.Read(ref _replicationSampleDroppedTotal),
                replicationLatency);
        }

        public void PrintFinal()
        {
            Console.WriteLine();
            Console.WriteLine("Final Cluster Soak Test Results");
            Console.WriteLine($"Elapsed: {_totalStopwatch.Elapsed:hh\\:mm\\:ss}");
            Console.WriteLine($"Writes succeeded: {Interlocked.Read(ref _writeSuccessTotal)}");
            Console.WriteLine($"Writes failed: {Interlocked.Read(ref _writeFailureTotal)}");
            Console.WriteLine($"Replication visible samples: {Interlocked.Read(ref _replicationVisibleTotal)}");
            Console.WriteLine($"Replication timeout samples: {Interlocked.Read(ref _replicationTimeoutTotal)}");
            Console.WriteLine($"Replication samples dropped: {Interlocked.Read(ref _replicationSampleDroppedTotal)}");
        }

        private static LatencySnapshot DrainLatencySnapshot(ConcurrentQueue<double> values)
        {
            List<double> drained = new List<double>();

            while (values.TryDequeue(out double value))
            {
                drained.Add(value);
            }

            return LatencySnapshot.From(drained);
        }
    }
}
