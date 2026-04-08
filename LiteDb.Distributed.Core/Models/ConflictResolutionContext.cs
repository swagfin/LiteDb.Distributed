

namespace LiteDb.Distributed.Core.Models
{
    public class ConflictResolutionContext
    {
        public required string LocalNodeId { get; set; }
        public required OperationRecord IncomingOperation { get; set; }
        public DocumentState? LocalDocumentState { get; set; }
    }

}
