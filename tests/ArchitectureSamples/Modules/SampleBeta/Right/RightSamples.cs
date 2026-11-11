using CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleAlpha.Contracts;
using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Modules;

namespace CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleBeta.Right;

/// <summary>Beta's entity: refers to SampleAlpha by id only.</summary>
[Module<SampleBetaModule>]
public class SampleBetaItem : BaseEntity
{
    public int SampleAlphaItemId { get; set; }
}

/// <summary>Uses only what SampleAlpha publishes: must pass rule 1, async and lambdas included.</summary>
public sealed class UsesAlphaContracts(IAlphaItemLookup lookup)
{
    public async Task<string?> NameOfAsync(int id, CancellationToken cancellationToken)
    {
        var summary = await lookup.FindAsync(id, cancellationToken);
        Func<AlphaItemSummary, string> name = s => s.Name;
        return summary is null ? null : name(summary);
    }
}
