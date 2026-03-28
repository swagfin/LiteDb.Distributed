using System.Threading;

namespace LiteDb.Distributed.Infrastructure.Context;

public sealed class DatabaseContextAccessor : IDatabaseContextAccessor
{
    private static readonly AsyncLocal<ScopeHolder?> AsyncLocalHolder = new();

    public DatabaseRequestContext? Current => AsyncLocalHolder.Value?.Context;

    public IDisposable BeginScope(DatabaseRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var previous = AsyncLocalHolder.Value;
        AsyncLocalHolder.Value = new ScopeHolder(context);

        return new ScopePopper(previous);
    }

    private sealed class ScopeHolder
    {
        public ScopeHolder(DatabaseRequestContext context)
        {
            Context = context;
        }

        public DatabaseRequestContext Context { get; }
    }

    private sealed class ScopePopper : IDisposable
    {
        private readonly ScopeHolder? _previous;
        private bool _disposed;

        public ScopePopper(ScopeHolder? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            AsyncLocalHolder.Value = _previous;
            _disposed = true;
        }
    }
}
