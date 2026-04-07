namespace LiteDb.Distributed.Studio.Models;

public sealed class WriteResultDto
{
    public string Collection { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime CommittedUtc { get; set; }
    public bool IsDeleted { get; set; }
}
