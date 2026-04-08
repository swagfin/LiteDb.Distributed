

namespace LiteDb.Distributed.Core.Models
{
    public class ConflictResolutionResult
    {
        public required ConflictResolutionAction Action { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

}
