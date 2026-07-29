namespace LiteDb.Distributed.Server.Infrastructure.Replication.Signals
{
    internal static class ReplicationSignalRetryPolicy
    {
        private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RetryMaxDelay = TimeSpan.FromSeconds(30);

        public static TimeSpan ComputeRetryDelay(int attempt)
        {
            int boundedAttempt = Math.Clamp(attempt, 1, 10);
            double exponent = Math.Pow(2, boundedAttempt - 1);
            double delayMs = Math.Min(RetryBaseDelay.TotalMilliseconds * exponent, RetryMaxDelay.TotalMilliseconds);
            int jitterMs = Random.Shared.Next(0, 250);
            return TimeSpan.FromMilliseconds(delayMs + jitterMs);
        }

        public static ScheduledDispatch MergeRetry(ScheduledDispatch existing, ScheduledDispatch retry)
        {
            if (existing.Attempt == 0 && existing.DueUtc <= DateTime.UtcNow)
            {
                return existing;
            }

            DateTime dueUtc = existing.DueUtc <= retry.DueUtc ? existing.DueUtc : retry.DueUtc;
            int attempt = existing.Attempt == 0 ? 0 : Math.Max(existing.Attempt, retry.Attempt);
            string reason = string.IsNullOrWhiteSpace(existing.Reason) ? retry.Reason : existing.Reason;

            return new ScheduledDispatch(existing.DatabaseName, reason, attempt, dueUtc, DateTime.UtcNow);
        }
    }
}
