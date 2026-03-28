namespace DistributedLiteDb.Core.SampleEntities;

public sealed record Item
{
    public required string Id { get; init; }
    public required string Sku { get; init; }
    public required string Name { get; init; }
    public decimal UnitPrice { get; init; }
    public DateTime UpdatedUtc { get; init; }
}
