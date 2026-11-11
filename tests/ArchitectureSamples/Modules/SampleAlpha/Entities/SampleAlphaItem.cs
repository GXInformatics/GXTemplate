using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Modules;

namespace CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleAlpha.Entities;

/// <summary>Module SampleAlpha's entity: internal to SampleAlpha, whatever its C# accessibility.</summary>
[Module<SampleAlphaModule>]
public class SampleAlphaItem : BaseEntity
{
    public string Name { get; set; } = string.Empty;
}
