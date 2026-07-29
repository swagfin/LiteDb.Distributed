namespace LiteDb.Distributed.Server.Core.Queries
{
    public class QueryRequest
    {
        public string Query { get; set; } = string.Empty;
        public int Take { get; set; } = 200;
    }
}
