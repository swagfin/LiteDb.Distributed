namespace LiteDb.Distributed.Server.Domain.Models
{
    public class OperationIngestionResult
    {
        public required int AcceptedCount { get; set; }
        public required int ConflictCount { get; set; }
    }
}
