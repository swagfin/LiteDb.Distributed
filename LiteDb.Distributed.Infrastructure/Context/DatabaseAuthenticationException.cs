namespace LiteDb.Distributed.Infrastructure.Context;

public sealed class DatabaseAuthenticationException : Exception
{
    public DatabaseAuthenticationException(string message)
        : base(message)
    {
    }
}
