// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Infrastructure.Persistence.Conversions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Configurations;

#nullable disable
public class AuditTrailConfiguration : IEntityTypeConfiguration<AuditTrail>
{
    public void Configure(EntityTypeBuilder<AuditTrail> builder)
    {
        builder.HasOne(x => x.Owner)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.Navigation(e => e.Owner).AutoInclude();

        // TenantId is a plain column with NO foreign key and no navigation, which is a deliberate
        // departure from how every other tenant reference in this model is configured.
        //
        // A relationship would have to choose a delete behaviour, and every choice is wrong here:
        // Cascade erases the audit trail of the tenant somebody just deleted - precisely the
        // evidence a deletion makes interesting - while Restrict makes deleting a tenant impossible
        // once anything in it has ever been audited, and SetNull silently rewrites history to say
        // the change belonged to nobody.
        //
        // An audit row records what was true when it was written. It must outlive the rows it
        // refers to, so it stores the id and asks the database to enforce nothing about it. The
        // index is here because any future per-tenant view filters on this column first.
        builder.HasIndex(t => t.TenantId);

        builder.Property(t => t.AuditType)
            .HasConversion<string>();
        builder.Property(u => u.Changes).HasJsonConversion();
        builder.Property(u => u.PrimaryKey).HasJsonConversion();
        builder.Ignore(x => x.TemporaryProperties);
        builder.Ignore(x => x.HasTemporaryProperties);

    }
}
