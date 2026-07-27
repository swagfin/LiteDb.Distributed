namespace LiteDb.Distributed.Server.Core.Models
{
    public class OperationReceiptPruneResult
    {
        public required int PrunedCount { get; set; }
        public DateTime? OldestPrunedUtc { get; set; }
        public DateTime? NewestPrunedUtc { get; set; }
    }
}
