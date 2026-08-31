// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CleanArchitecture.Blazor.Infrastructure.Persistence.Configurations;

public class SecurityPolicyConfiguration : IEntityTypeConfiguration<SecurityPolicy>
{
    public void Configure(EntityTypeBuilder<SecurityPolicy> builder)
    {
        // Named explicitly so the GX naming convention yields. SecurityPolicy derives from
        // BaseAuditableEntity and is therefore an IBusinessEntity, but it is one of the TEMPLATE's
        // tables rather than this business's: it stays "SecurityPolicies" in the default schema
        // instead of becoming core."TBL_SECURITY_POLICY", for the same reason Documents does - the
        // core schema is where a project's own models live, and a template upgrade must never hand
        // an existing project a rename migration. TemplateTablesStayOutOfCoreTests pins this.
        builder.ToTable("SecurityPolicies");

        builder.Ignore(e => e.DomainEvents);
    }
}
