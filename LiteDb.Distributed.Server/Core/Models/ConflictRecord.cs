

namespace LiteDb.Distributed.Server.Core.Models
{
    public class ConflictRecord
    {
        public required string Id { get; set; }
        public required string NodeId { get; set; }
        public required string Collection { get; set; }
        public required string EntityId { get; set; }
        public required string IncomingOperationId { get; set; }
        public required string LocalVersion { get; set; }
        public required string IncomingVersionHint { get; set; }
        public required string Reason { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string? LocalPayload { get; set; }
        public string? IncomingPayload { get; set; }
    }

}
