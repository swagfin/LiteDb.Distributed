namespace LiteDb.Distributed.Tests.TestEntities
{
    public class Customer
    {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public required string Email { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}
