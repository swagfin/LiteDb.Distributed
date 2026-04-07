

namespace LiteDb.Distributed.Infrastructure.Storage
{
    public sealed record LogicalDatabaseRegistration
    {
        public required string DatabaseName { get; init; }
        public required string Credential { get; init; }
        public required DateTime CreatedUtc { get; init; }
        public required DateTime UpdatedUtc { get; init; }
    }

}
