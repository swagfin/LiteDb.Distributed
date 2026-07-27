using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Core.Filters
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class RequireClientDatabaseAuthAttribute : TypeFilterAttribute
    {
        public RequireClientDatabaseAuthAttribute() : base(typeof(RequireClientDatabaseAuthFilter))
        {
        }
    }
}
