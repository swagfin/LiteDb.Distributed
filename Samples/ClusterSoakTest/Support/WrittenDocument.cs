namespace ClusterSoakTest.Support
{
    public class WrittenDocument
    {
        public string WriterNodeName { get; set; } = string.Empty;
        public string DocumentId { get; set; } = string.Empty;
        public DateTime WrittenUtc { get; set; }
    }
}
