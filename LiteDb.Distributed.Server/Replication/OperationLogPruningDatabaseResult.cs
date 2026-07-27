namespace LiteDb.Distributed.Server.Replication
{
    public class OperationLogPruningDatabaseResult
    {
        public required string DatabaseName { get; set; }
        public required string Status { get; set; }
        public string? Reason { get; set; }
        public long PruneThroughLogSequence { get; set; }
        public int PrunedCount { get; set; }
        public long MaxPrunedLogSequence { get; set; }
        public int PrunedReceiptCount { get; set; }
    }
}
