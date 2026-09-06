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
        // TWO indexes, because one cannot cover both partitions. The first constrains each TENANT's
        // rows. It cannot constrain the SHARED ones, whose key is a NULL: SQLite and PostgreSQL
        // treat NULLs as DISTINCT in a unique index, so two shared rows with the same name and value
        // do not collide.
        //
        // SQL SERVER IS NOT THE EXCEPTION PASS 32 THOUGHT IT WAS, and this was measured rather than
        // reasoned. That pass recorded "SQL Server treats NULLs as equal and does block it". It does
        // not get the chance: EF emits its own filter on a unique index over nullable columns, and
        // sys.indexes shows the first index below created as
        //
        //     IX_PicklistSets_TenantId_Name_Value  filter: ([TenantId] IS NOT NULL AND [Value] IS NOT NULL)
        //
        // so shared rows are not in that index at all. The gap was on all three providers, not two.
        builder.HasIndex(t => new { t.TenantId, t.Name, t.Value }).IsUnique(true);

        // The shared partition, closed in Pass 40. ONE filter string for all three providers: EF
        // emits it verbatim, and `"TenantId" IS NULL` is ANSI identifier quoting that SQLite and
        // PostgreSQL take as written and SQL Server normalises to ([TenantId] IS NULL) - verified by
        // applying the migration to LocalDB and reading sys.indexes back, not by assuming it. That
        // is why this is one HasFilter and not a per-provider branch.
        //
        // It matters more than Pass 32 judged. That pass called the gap narrow because shared rows
        // came only from idempotent seeding or from a holder who also had no tenant; Pass 33 §C then
        // gave a TENANT-SCOPED ManageShared holder a switch in the create dialog, so there has been
        // an ordinary UI path to a duplicate ever since - and a duplicated shared value appears
        // twice in every tenant's picker.
        builder.HasIndex(t => new { t.Name, t.Value }).IsUnique(true).HasFilter("\"TenantId\" IS NULL");
        builder.Ignore(e => e.DomainEvents);
    }
}
