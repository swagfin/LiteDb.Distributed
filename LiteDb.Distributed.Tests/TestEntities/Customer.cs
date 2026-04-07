namespace LiteDb.Distributed.Tests
{
    public record Customer
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Email { get; init; }
        public DateTime UpdatedUtc { get; init; }
    }
}
