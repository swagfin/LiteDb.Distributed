using Microsoft.AspNetCore.Http;

namespace LiteDb.Distributed.Server.Core.Context
{
    public interface IDatabaseRequestContextResolver
    {
        Task<DatabaseRequestContext> ResolveAsync(IHeaderDictionary headers, CancellationToken cancellationToken = default);
    }

}
