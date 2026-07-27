

namespace LiteDb.Distributed.Server.Domain.Models
{
    public class NodeMetadataRecord
    {
        public required string NodeId { get; set; }
        public long LastLocalSequence { get; set; }
        public DateTime LastWriteUtc { get; set; }
    }

}
