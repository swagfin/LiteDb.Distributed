namespace DistributedLiteDb.Core.Exceptions;

public sealed class VersionMismatchException : Exception
{
    public VersionMismatchException(string message)
        : base(message)
    {
    }
}
