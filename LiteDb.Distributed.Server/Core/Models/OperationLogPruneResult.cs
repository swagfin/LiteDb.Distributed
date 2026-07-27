namespace LiteDb.Distributed.Server.Core.Models
{
    public class OperationLogPruneResult
    {
        public required int PrunedCount { get; set; }
        public long MaxPrunedLogSequence { get; set; }
    }
}
