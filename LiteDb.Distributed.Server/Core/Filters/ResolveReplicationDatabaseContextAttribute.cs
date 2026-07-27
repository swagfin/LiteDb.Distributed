using Microsoft.AspNetCore.Mvc;

namespace LiteDb.Distributed.Server.Core.Filters
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class ResolveNodeReplicationDatabaseAttribute : TypeFilterAttribute
    {
        public ResolveNodeReplicationDatabaseAttribute() : base(typeof(ResolveNodeReplicationDatabaseFilter))
        {
        }
    }
}
