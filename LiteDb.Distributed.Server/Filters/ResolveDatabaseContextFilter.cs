using LiteDb.Distributed.Infrastructure.Context;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LiteDb.Distributed.Server.Filters
{
    public class RequireClientDatabaseAuthFilter : IAsyncActionFilter
    {
        private readonly IDatabaseRequestContextResolver _contextResolver;
        private readonly IDatabaseContextAccessor _contextAccessor;
        private readonly ILogger<RequireClientDatabaseAuthFilter> _logger;

        public RequireClientDatabaseAuthFilter(IDatabaseRequestContextResolver contextResolver, IDatabaseContextAccessor contextAccessor, ILogger<RequireClientDatabaseAuthFilter> logger)
        {
            _contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
            _contextAccessor = contextAccessor ?? throw new ArgumentNullException(nameof(contextAccessor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            try
            {
                DatabaseRequestContext databaseContext = await _contextResolver.ResolveAsync(context.HttpContext.Request.Headers, context.HttpContext.RequestAborted).ConfigureAwait(false);
                using IDisposable scope = _contextAccessor.BeginScope(databaseContext);
                await next().ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Database authorization failed. Method={Method} Path={Path}", context.HttpContext.Request.Method, context.HttpContext.Request.Path.Value);
                context.Result = new ObjectResult(new { Error = ex.Message })
                {
                    StatusCode = StatusCodes.Status403Forbidden
                };
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Database context resolution rejected request. Method={Method} Path={Path}", context.HttpContext.Request.Method, context.HttpContext.Request.Path.Value);
                context.Result = new ObjectResult(new { Error = ex.Message })
                {
                    StatusCode = StatusCodes.Status400BadRequest
                };
            }
        }
    }
}
