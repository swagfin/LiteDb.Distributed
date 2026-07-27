using System.Diagnostics;

namespace DistributedCacheProbe.Support
{
    public class CacheProbeMetrics
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly List<double> _allPeerVisibilityMilliseconds = new List<double>();
        private long _writesSucceeded;
        private long _writesFailed;
        private long _allPeerVisible;
        private long _allPeerTimeout;

        public void Record(CacheProbeIterationResult result)
        {
            if (!result.WriteSucceeded)
            {
                _writesFailed++;
                return;
            }

            _writesSucceeded++;

            if (result.AllPeersVisibleElapsed is TimeSpan elapsed)
            {
                _allPeerVisible++;
                _allPeerVisibilityMilliseconds.Add(elapsed.TotalMilliseconds);
            }
            else
            {
                _allPeerTimeout++;
            }
        }

        public void PrintFinal()
        {
            Console.WriteLine();
            Console.WriteLine("Final Distributed Cache Probe Results");
            Console.WriteLine($"Elapsed: {_stopwatch.Elapsed:hh\\:mm\\:ss}");
            Console.WriteLine($"Writes succeeded: {_writesSucceeded}");
            Console.WriteLine($"Writes failed: {_writesFailed}");
            Console.WriteLine($"Keys visible on all peer nodes: {_allPeerVisible}");
            Console.WriteLine($"Keys timed out before all peers saw them: {_allPeerTimeout}");
            Console.WriteLine($"All-peer visibility p50/p95/p99: {FormatPercentiles(_allPeerVisibilityMilliseconds)}");
        }

        private static string FormatPercentiles(List<double> values)
        {
            if (values.Count == 0)
            {
                return "n/a";
            }

            values.Sort();
            return $"{Percentile(values, 0.50d):F0}/{Percentile(values, 0.95d):F0}/{Percentile(values, 0.99d):F0}ms";
        }

        private static double Percentile(List<double> sortedValues, double percentile)
        {
            int index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
            index = Math.Clamp(index, 0, sortedValues.Count - 1);
            return sortedValues[index];
        }
    }
}
