namespace LiteDb.Distributed.Server.Domain.Models
{
    public class OperationLogPruneResult
    {
        public required int PrunedCount { get; set; }
        public long MaxPrunedLogSequence { get; set; }
    }
}
