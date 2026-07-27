

namespace LiteDb.Distributed.Server.Context
{
    public interface IDatabaseContextAccessor
    {
        DatabaseRequestContext? Current { get; }

        IDisposable BeginScope(DatabaseRequestContext context);
    }

}
