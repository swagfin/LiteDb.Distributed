namespace LiteDb.Distributed.Infrastructure.Context;

public sealed record DatabaseRequestContext
{
    public required string DatabaseName { get; init; }
    public required string Credential { get; init; }
}
