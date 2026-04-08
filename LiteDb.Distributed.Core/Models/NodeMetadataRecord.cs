

namespace LiteDb.Distributed.Core.Models
{
    public class NodeMetadataRecord
    {
        public required string NodeId { get; set; }
        public long LastLocalSequence { get; set; }
        public DateTime LastWriteUtc { get; set; }
    }

}
