using LiteDb.Distributed.Server.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LiteDb.Distributed.Server.Core.Filters
{
    public class RequireReplicationApiKeyFilter : IAsyncActionFilter
    {
        private readonly ClusterNodeOptions _nodeOptions;

        public RequireReplicationApiKeyFilter(ClusterNodeOptions nodeOptions)
        {
            _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            string providedReplicationApiKey = context.HttpContext.Request.Headers["ReplicationApiKey"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(providedReplicationApiKey) || !string.Equals(providedReplicationApiKey, _nodeOptions.ReplicationApiKey, StringComparison.Ordinal))
            {
                context.Result = new ObjectResult(new { Error = "ReplicationApiKey is invalid." })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
                return;
            }

            await next().ConfigureAwait(false);
        }
    }
}
