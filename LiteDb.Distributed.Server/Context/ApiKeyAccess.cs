namespace LiteDb.Distributed.Server.Context
{
    public class ApiKeyAccess
    {
        public required string ApiKey { get; set; }
        public required bool IsRoot { get; set; }
        public required bool CanAddDatabase { get; set; }
        public required bool CanDeleteDatabase { get; set; }
        public required bool CanReadDocument { get; set; }
        public required bool CanWriteDocument { get; set; }
        public required bool CanUpdateDocument { get; set; }
        public required bool CanDeleteDocument { get; set; }
    }
}
