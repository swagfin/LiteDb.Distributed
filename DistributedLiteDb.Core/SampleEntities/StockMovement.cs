namespace DistributedLiteDb.Core.SampleEntities;

public sealed record StockMovement
{
    public required string Id { get; init; }
    public required string ItemId { get; init; }
    public required decimal QuantityDelta { get; init; }
    public required string Reason { get; init; }
    public DateTime MovementUtc { get; init; }
}
