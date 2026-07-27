namespace LiteDb.Distributed.Server.Core.Models
{
    public class OperationIngestionResult
    {
        public required int ProcessedCount { get; set; }
        public required int AcceptedCount { get; set; }
        public required int ConflictCount { get; set; }
        public long LastProcessedLogSequence { get; set; }
    }
}
