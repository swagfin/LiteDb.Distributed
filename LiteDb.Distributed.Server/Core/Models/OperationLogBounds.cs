namespace LiteDb.Distributed.Server.Core.Models
{
    public class OperationLogBounds
    {
        public long OldestLogSequence { get; set; }
        public long NewestLogSequence { get; set; }
        public bool HasOperations => OldestLogSequence > 0 && NewestLogSequence > 0;
    }
}
