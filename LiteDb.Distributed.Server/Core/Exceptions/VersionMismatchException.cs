

namespace LiteDb.Distributed.Server.Core.Exceptions
{
    public class VersionMismatchException : Exception
    {
        public VersionMismatchException(string message) : base(message)
        {
        }
    }

}
