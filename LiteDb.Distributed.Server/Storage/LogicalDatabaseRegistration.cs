

namespace LiteDb.Distributed.Server.Storage
{
    public class LogicalDatabaseRegistration
    {
        public required string DatabaseName { get; set; }
        public required DateTime CreatedUtc { get; set; }
        public required DateTime UpdatedUtc { get; set; }
    }

}
