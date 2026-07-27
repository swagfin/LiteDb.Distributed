

namespace LiteDb.Distributed.Server.Core.Context
{
    public interface IDatabaseContextAccessor
    {
        DatabaseRequestContext? Current { get; }

        IDisposable BeginScope(DatabaseRequestContext context);
    }

}
