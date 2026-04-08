using Microsoft.AspNetCore.Http;

namespace LiteDb.Distributed.Infrastructure.Context
{
    public interface IDatabaseRequestContextResolver
    {
        Task<DatabaseRequestContext> ResolveAsync(IHeaderDictionary headers, CancellationToken cancellationToken = default);
    }

}
