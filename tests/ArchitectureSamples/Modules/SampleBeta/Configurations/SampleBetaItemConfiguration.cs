using CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleAlpha.Entities;
using CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleBeta.Right;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CleanArchitecture.Blazor.ArchitectureSamples.Modules.SampleBeta.Configurations;

/// <summary>The one sanctioned use of another module's entity: a foreign key without a navigation (decision 3).</summary>
public sealed class SampleBetaItemConfiguration : IEntityTypeConfiguration<SampleBetaItem>
{
    public void Configure(EntityTypeBuilder<SampleBetaItem> builder) =>
        builder.HasOne<SampleAlphaItem>().WithMany().HasForeignKey(b => b.SampleAlphaItemId).OnDelete(DeleteBehavior.Restrict);
}
