// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using CleanArchitecture.Blazor.Domain.Identity;
using Microsoft.EntityFrameworkCore.Infrastructure;

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

    /// <summary>
    /// The context's database facade, so a handler can own a transaction -
    /// <c>await using var tx = await db.Database.BeginTransactionAsync(ct);</c> - without casting to
    /// the concrete context (Pass 55). Implemented by <c>DbContext.Database</c> itself.
    /// </summary>
    /// <remarks>
    /// <c>AuditableEntityInterceptor</c> opens a transaction only when none is current, so audit
    /// rows join the handler's transaction and roll back with it. A handler that needs a document
    /// header and its lines - or a stock movement and its balance - to commit together is the reason
    /// this exists. It is the transaction surface, not an invitation to raw SQL from Application.
    /// </remarks>
    DatabaseFacade Database { get; }
}
