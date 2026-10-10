// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Configurations;

public class TenantUserConfiguration : IEntityTypeConfiguration<TenantUser>
{
    public void Configure(EntityTypeBuilder<TenantUser> builder)
    {
        builder.HasOne(tu => tu.Tenant)
               .WithMany(t => t.TenantUsers)
               .HasForeignKey(tu => tu.TenantId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(tu => tu.User).WithMany(x=>x.TenantUsers)
                .HasForeignKey(tu => tu.UserId)
                .OnDelete(DeleteBehavior.Cascade);

        // One membership row per (tenant, user) - Pass 54. Nothing stopped a second one, and every
        // reader of TenantUsers (the loader's AllowedTenantIds, the switcher's list, the user grid)
        // had to Distinct() or double-count. Tenant first, so it also serves "members of tenant X"
        // and replaces the TenantId-only index EF created for the foreign key. The migration removes
        // any duplicates an existing database already holds before creating it.
        builder.HasIndex(tu => new { tu.TenantId, tu.UserId }).IsUnique();
    }
}