using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Core.Filters
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class RequireNodeReplicationApiKeyAttribute : TypeFilterAttribute
    {
        public RequireNodeReplicationApiKeyAttribute() : base(typeof(RequireReplicationApiKeyFilter))
        {
        }
    }
}
