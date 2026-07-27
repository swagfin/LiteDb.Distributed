

namespace LiteDb.Distributed.Server.Core.Models
{
    public class DocumentState
    {
        public required string Collection { get; set; }
        public required string EntityId { get; set; }
        public required string Version { get; set; }
        public required string LastWriterNodeId { get; set; }
        public required DateTime LastModifiedUtc { get; set; }
        public required bool IsDeleted { get; set; }
        public required string Payload { get; set; }
    }

}
