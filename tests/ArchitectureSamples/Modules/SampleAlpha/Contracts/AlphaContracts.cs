namespace CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleAlpha.Contracts;

/// <summary>What SampleAlpha publishes: a DTO and a query interface other modules may use.</summary>
public sealed record AlphaItemSummary(int Id, string Name);

public interface IAlphaItemLookup
{
    Task<AlphaItemSummary?> FindAsync(int id, CancellationToken cancellationToken);
}
