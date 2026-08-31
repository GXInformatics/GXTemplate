// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Domain.Identity;

namespace CleanArchitecture.Blazor.Application.Common.Interfaces;

public interface IApplicationDbContext: IAsyncDisposable
{
    // SystemLog is deliberately absent: logs live in their own database behind ILogDbContext, so
    // that no query written against the business context can join across the two.
    DbSet<AuditTrail> AuditTrails { get; set; }
    DbSet<Document> Documents { get; set; }
    DbSet<PicklistSet> PicklistSets { get; set; }

    /// <summary>The administered security policy - one row. See <c>SecurityPolicy</c>.</summary>
    DbSet<SecurityPolicy> SecurityPolicies { get; set; }
    DbSet<Tenant> Tenants { get; set; }
    DbSet<TenantUser> TenantUsers { get; set; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
