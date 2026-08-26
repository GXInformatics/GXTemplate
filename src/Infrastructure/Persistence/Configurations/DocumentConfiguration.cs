// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.


using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Configurations;

public class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.Property(t => t.DocumentType).HasConversion<string>();
        // The /files endpoint resolves a document by its storage key on every request for a document
        // object, so that lookup gets an index. Not unique: derive-and-retry keeps live keys distinct
        // without the database having to refuse an insert to prove it.
        builder.HasIndex(t => t.StorageKey);
        builder.Ignore(e => e.DomainEvents);
        builder.HasOne(x => x.CreatedBy)
            .WithMany()
            .HasForeignKey(x => x.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.LastModifiedBy)
            .WithMany()
            .HasForeignKey(x => x.LastModifiedById)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(e => e.CreatedBy).AutoInclude();
        builder.Navigation(e => e.LastModifiedBy).AutoInclude();
        builder.Navigation(e => e.Tenant).AutoInclude();
    }
}
