using Microsoft.AspNetCore.Http;

namespace LiteDb.Distributed.Server.Context
{
    public interface IDatabaseRequestContextResolver
    {
        Task<DatabaseRequestContext> ResolveAsync(IHeaderDictionary headers, CancellationToken cancellationToken = default);
    }

}
