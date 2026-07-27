using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Core.Context;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LiteDb.Distributed.Server.Core.Filters
{
    public class ResolveNodeReplicationDatabaseFilter : IAsyncActionFilter
    {
        private readonly ClusterNodeOptions _nodeOptions;
        private readonly IDatabaseContextAccessor _contextAccessor;
        private readonly ILogger<ResolveNodeReplicationDatabaseFilter> _logger;

        public ResolveNodeReplicationDatabaseFilter(ClusterNodeOptions nodeOptions, IDatabaseContextAccessor contextAccessor, ILogger<ResolveNodeReplicationDatabaseFilter> logger)
        {
            _nodeOptions = nodeOptions ?? throw new ArgumentNullException(nameof(nodeOptions));
            _contextAccessor = contextAccessor ?? throw new ArgumentNullException(nameof(contextAccessor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            try
            {
                string rawDatabaseName = context.HttpContext.Request.Headers["Database"].ToString();
                string normalizedDatabaseName = DatabaseNameNormalizer.Normalize(rawDatabaseName);

                using IDisposable scope = _contextAccessor.BeginScope(new DatabaseRequestContext
                {
                    DatabaseName = normalizedDatabaseName,
                    ApiKey = _nodeOptions.ReplicationApiKey,
                    IsRoot = true,
                    CanAddDatabase = true,
                    CanDeleteDatabase = true,
                    CanReadDocument = true,
                    CanWriteDocument = true,
                    CanUpdateDocument = true,
                    CanDeleteDocument = true
                });

                await next().ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Replication context resolution rejected request. Method={Method} Path={Path}", context.HttpContext.Request.Method, context.HttpContext.Request.Path.Value);
                context.Result = new ObjectResult(new { Error = ex.Message })
                {
                    StatusCode = StatusCodes.Status400BadRequest
                };
            }
        }
    }
}
