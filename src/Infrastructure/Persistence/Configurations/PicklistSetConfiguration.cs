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
        // Deliberately still (Name, Value) and NOT (TenantId, Name, Value), even though the entity
        // now carries a TenantId. Widening the key would let two tenants define the same
        // Name/Value pair, which is a behaviour change - and this pass stamps without scoping.
        // Whoever scopes picklists has to widen this index in the same change, or the first two
        // tenants to want the same brand name will collide on a constraint that has no business
        // spanning them.
        builder.HasIndex(t => new { t.Name, t.Value }).IsUnique(true);
        builder.Ignore(e => e.DomainEvents);
    }
}
