using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Configurations;

/// <summary>The idempotency table (Pass 58). See <c>IdempotencyStore</c>.</summary>
public class ProcessedRequestConfiguration : IEntityTypeConfiguration<ProcessedRequest>
{
    public void Configure(EntityTypeBuilder<ProcessedRequest> builder)
    {
        builder.ToTable("ProcessedRequests");
        builder.HasKey(x => x.RequestId);
        builder.Property(x => x.RequestId).ValueGeneratedNever();
        builder.Property(x => x.RequestType).IsRequired();
        // Unbounded: the model-wide 450 default is for keys and names, not a stored response. Plain
        // text rather than jsonb because this model also generates the other providers' migrations.
        builder.Property(x => x.Response).Metadata.SetMaxLength(null);
        builder.HasIndex(x => x.CreatedAt);
    }
}

/// <summary>The gap-free counters (Pass 58). See <c>DocumentNumberAllocator</c>.</summary>
public class DocumentSequenceConfiguration : IEntityTypeConfiguration<DocumentSequence>
{
    public void Configure(EntityTypeBuilder<DocumentSequence> builder)
    {
        builder.ToTable("DocumentSequences");
        builder.HasKey(x => new { x.DocumentType, x.BranchId, x.Year });
        builder.Property(x => x.DocumentType).HasMaxLength(64);
    }
}
