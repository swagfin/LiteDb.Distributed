namespace LiteDb.Distributed.Core.Models;

public enum ConflictResolutionAction
{
    ApplyIncoming = 1,
    KeepLocal = 2,
    KeepLocalAndRecordConflict = 3
}

