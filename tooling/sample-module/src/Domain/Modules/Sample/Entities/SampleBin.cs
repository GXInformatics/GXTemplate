using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Modules;

namespace CleanArchitecture.Blazor.Domain.Modules.Sample.Entities;

/// <summary>A storage bin in one branch: the sample module's entity, in smp."TBL_SAMPLE_BIN".</summary>
[Module<SampleModule>]
public class SampleBin : BaseEntity<Guid>, IMustHaveBranch
{
    public SampleBin() => Id = Guid.CreateVersion7();

    public int BranchId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Set by the module's own posting-event handler, in the transaction that created the bin.</summary>
    public bool Announced { get; set; }
}
