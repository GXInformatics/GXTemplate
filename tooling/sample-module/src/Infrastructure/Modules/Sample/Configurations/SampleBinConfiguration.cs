using CleanArchitecture.Blazor.Domain.Modules.Sample.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CleanArchitecture.Blazor.Infrastructure.Modules.Sample.Configurations;

/// <summary>The sample bin's table. It brings the entity into the model: a module entity has no DbSet property.</summary>
public class SampleBinConfiguration : IEntityTypeConfiguration<SampleBin>
{
    public void Configure(EntityTypeBuilder<SampleBin> builder)
    {
        builder.Property(b => b.Name).HasMaxLength(100).IsRequired();
        builder.HasOne<Branch>().WithMany().HasForeignKey(b => b.BranchId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(b => b.DomainEvents);
    }
}
