

namespace LiteDb.Distributed.Server.Domain.Exceptions
{
    public class VersionMismatchException : Exception
    {
        public VersionMismatchException(string message) : base(message)
        {
        }
    }

}
