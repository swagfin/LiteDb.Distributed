namespace ClusterSoakTest.Support
{
    public class LatencySnapshot
    {
        public double? P50 { get; set; }
        public double? P95 { get; set; }
        public double? P99 { get; set; }

        public static LatencySnapshot From(List<double> values)
        {
            if (values.Count == 0)
            {
                return new LatencySnapshot();
            }

            values.Sort();
            return new LatencySnapshot
            {
                P50 = Percentile(values, 0.50d),
                P95 = Percentile(values, 0.95d),
                P99 = Percentile(values, 0.99d)
            };
        }

        public override string ToString()
        {
            if (P50 is null || P95 is null || P99 is null)
            {
                return "n/a";
            }

            return $"{P50:F0}/{P95:F0}/{P99:F0}ms";
        }

        private static double Percentile(List<double> sortedValues, double percentile)
        {
            int index = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
            index = Math.Clamp(index, 0, sortedValues.Count - 1);
            return sortedValues[index];
        }
    }
}
