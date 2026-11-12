using CleanArchitecture.Blazor.Domain.Common.Entities;
using CleanArchitecture.Blazor.Domain.Common.Modules;

namespace CleanArchitecture.Blazor.Domain.Modules.Sample.Entities;

/// <summary>
/// A storage bin in one branch: the sample module's first entity, in core."TBL_SAMPLE_BINS" (Pass 60c: core, plural,
/// snake_case columns, an int key - it was a Guid v7 until 1.7.1).
/// </summary>
[Module<SampleModule>]
public class SampleBin : BaseEntity, IMustHaveBranch
{
    public int BranchId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Set by the module's own posting-event handler, in the transaction that created the bin.</summary>
    public bool Announced { get; set; }
}
