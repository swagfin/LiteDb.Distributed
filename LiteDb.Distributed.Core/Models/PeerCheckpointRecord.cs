namespace LiteDb.Distributed.Core.Models
{
    public class PeerCheckpointRecord
    {
        public required string LocalNodeId { get; set; }
        public required string PeerNodeId { get; set; }
        public long LastPushedLocalLogSequence { get; set; }
        public long LastPulledPeerLogSequence { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
