using System.Text.Json;

namespace LiteDb.Distributed.Studio.Models
{
    public class QueryResponseDto
    {
        public string Query { get; set; } = string.Empty;
        public int RequestedTake { get; set; }
        public int ReturnedRows { get; set; }
        public List<Dictionary<string, JsonElement>> Rows { get; set; } = [];
    }

}
