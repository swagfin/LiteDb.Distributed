namespace LiteDb.Distributed.Server.Infrastructure.Replication.Signals
{
    internal class ScheduledDispatch
    {
        public ScheduledDispatch(string databaseName, string reason, int attempt, DateTime dueUtc, DateTime updatedUtc)
        {
            DatabaseName = databaseName;
            Reason = reason;
            Attempt = attempt;
            DueUtc = dueUtc;
            UpdatedUtc = updatedUtc;
        }

        public string DatabaseName { get; set; }
        public string Reason { get; set; }
        public int Attempt { get; set; }
        public DateTime DueUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
