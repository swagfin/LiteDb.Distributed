

namespace LiteDb.Distributed.Infrastructure.Context
{
    public interface IDatabaseContextAccessor
    {
        DatabaseRequestContext? Current { get; }

        IDisposable BeginScope(DatabaseRequestContext context);
    }

}
