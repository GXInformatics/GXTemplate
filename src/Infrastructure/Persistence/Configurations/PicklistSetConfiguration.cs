// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.


using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Configurations;

public class PicklistSetConfiguration : IEntityTypeConfiguration<PicklistSet>
{
    public void Configure(EntityTypeBuilder<PicklistSet> builder)
    {
        // Named explicitly so the GX naming convention yields - see DocumentConfiguration for why
        // the template's own tables stay out of the core schema.
        builder.ToTable("PicklistSets");

        builder.Property(t => t.Name).HasConversion<string>().HasMaxLength(30);
        builder.Property(t => t.Value).HasMaxLength(50);
        builder.Property(t => t.Text).HasMaxLength(100);
        builder.Property(t => t.Description).HasMaxLength(255);
        // (TenantId, Name, Value) since Pass 32. It was (Name, Value) and Pass 24 left a comment
        // saying whoever scoped picklists had to widen it in the same change - "or the first two
        // tenants to want the same brand name will collide on a constraint that has no business
        // spanning them". Pass 31 scoped them and did not widen it, so that is exactly what
        // happened: the import's duplicate check said "not a duplicate" and the insert then failed
        // on the index.
        //
        // THE LESSON, which is more general than this entity: a query filter narrows what a query
        // SEES; a unique index constrains what the table HOLDS. Scoping reads does not scope
        // constraints, and a duplicate check written against the filtered view disagrees with the
        // index precisely when the hidden rows are the ones that matter.
        //
        // KNOWN GAP, deliberately left rather than papered over. This does not stop the SHARED
        // partition holding the same value twice on SQLite or PostgreSQL, because both treat NULLs
        // as distinct in a unique index; SQL Server treats them as equal and does block it. Closing
        // it portably needs a second, partial unique index over (Name, Value) WHERE TenantId IS
        // NULL, whose filter SQL differs per provider. It is narrow - shared rows come from seeding,
        // which is idempotent, or from a PicklistSets.ManageShared holder who also has no tenant -
        // and PicklistTenantUniquenessTests names it so it cannot widen unnoticed.
        builder.HasIndex(t => new { t.TenantId, t.Name, t.Value }).IsUnique(true);
        builder.Ignore(e => e.DomainEvents);
    }
}
