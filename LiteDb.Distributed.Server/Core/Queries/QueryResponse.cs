namespace LiteDb.Distributed.Server.Core.Queries
{
    public class QueryResponse
    {
        public required string Query { get; set; }
        public required int RequestedTake { get; set; }
        public required int MatchedCount { get; set; }
        public required int AppliedCount { get; set; }
        public required int ReturnedRows { get; set; }
        public IReadOnlyList<Dictionary<string, object?>> Rows { get; set; } = Array.Empty<Dictionary<string, object?>>();
    }
}
