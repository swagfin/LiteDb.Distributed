namespace LiteDb.Distributed.Infrastructure.Context
{
    public class ApiKeyAccess
    {
        public required string ApiKey { get; init; }
        public required bool IsRoot { get; init; }
        public required bool CanAddDatabase { get; init; }
        public required bool CanDeleteDatabase { get; init; }
        public required bool CanReadDocument { get; init; }
        public required bool CanWriteDocument { get; init; }
        public required bool CanUpdateDocument { get; init; }
        public required bool CanDeleteDocument { get; init; }
    }
}
